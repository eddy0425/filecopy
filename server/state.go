package main

import (
	"encoding/json"
	"errors"
	"io/fs"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"slices"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"
	"unicode/utf8"
)

const (
	internalDirName = ".filecopy" // server state and the recycle bin; hidden from clients like .uploads
	onlineWindow    = 60 * time.Second
	speedWindow     = 5 // seconds averaged for the live transfer speed
)

// Device is a client machine, identified by the EquipmentId it sends in X-Equipment-Id.
type Device struct {
	FirstSeen int64  `json:"firstSeen"` // unix ms
	LastSeen  int64  `json:"lastSeen"`
	LastIP    string `json:"lastIP"`
	BytesUp   int64  `json:"bytesUp"`
	BytesDown int64  `json:"bytesDown"`
	ResetAt   int64  `json:"resetAt"` // when the traffic counters were last zeroed (unix ms)

	ips   map[string]int64 // recent source IPs -> last seen (unix ms); several at once means an ID clash
	speed speedMeter
}

// speedMeter keeps per-second byte counts for the last few seconds.
type speedMeter struct {
	sec      [speedWindow]int64
	up, down [speedWindow]int64
}

func (m *speedMeter) add(sec, up, down int64) {
	i := sec % speedWindow
	if m.sec[i] != sec {
		m.sec[i], m.up[i], m.down[i] = sec, 0, 0
	}
	m.up[i] += up
	m.down[i] += down
}

// rates returns the average bytes/second over the last speedWindow seconds.
func (m *speedMeter) rates(sec int64) (up, down int64) {
	for i := range m.sec {
		if m.sec[i] > sec-speedWindow && m.sec[i] <= sec {
			up += m.up[i]
			down += m.down[i]
		}
	}
	return up / speedWindow, down / speedWindow
}

type persistedState struct {
	Paused            bool               `json:"paused"` // client connections refused
	TOTPSecret        string             `json:"totpSecret,omitempty"`
	AdminPasswordHash string             `json:"adminPasswordHash,omitempty"`
	ReadOnlyFolders   []string           `json:"readOnlyFolders,omitempty"`
	Devices           map[string]*Device `json:"devices"`
	// Renames maps an EquipmentId renamed in the panel to its new value until that client adopts it.
	Renames map[string]string `json:"renames,omitempty"`
}

// stateStore is the server's mutable state, persisted to .filecopy/state.json
// (config.json is mounted read-only under Docker).
type stateStore struct {
	path string
	now  func() time.Time

	mu         sync.Mutex
	st         persistedState
	dirty      bool           // traffic counters changed since the last save
	usedSteps  map[int64]bool // TOTP time steps already accepted, so a code cannot be replayed
	usedSecret string         // the secret usedSteps belong to

	// foldCase: the filesystem ignores case, so "A" and "a" are the same folder when following
	// renames and deletes of read-only folders. Enforcement always ignores case (the Windows
	// client does), which at worst over-restricts on case-sensitive filesystems.
	foldCase bool

	// gen changes whenever client connections are cut; in-flight transfers compare against it.
	gen atomic.Int64
}

func loadState(path string, now func() time.Time) (*stateStore, error) {
	s := &stateStore{path: path, now: now, foldCase: runtime.GOOS == "windows" || runtime.GOOS == "darwin"}
	data, err := os.ReadFile(path)
	if err != nil && !errors.Is(err, fs.ErrNotExist) {
		return nil, err
	}
	if err == nil {
		// A corrupt file must not silently drop the TOTP secret or the read-only folders.
		if err := json.Unmarshal(data, &s.st); err != nil {
			return nil, errors.New("parse " + path + ": " + err.Error())
		}
	}
	if s.st.Devices == nil {
		s.st.Devices = map[string]*Device{}
	}
	if s.st.Renames == nil {
		s.st.Renames = map[string]string{}
	}
	return s, nil
}

func (s *stateStore) saveLocked() error {
	data, err := json.MarshalIndent(&s.st, "", "  ")
	if err != nil {
		return err
	}
	tmp := s.path + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return err
	}
	if err := os.Rename(tmp, s.path); err != nil {
		return err
	}
	s.dirty = false
	return nil
}

// update applies fn and saves immediately; for settings changes.
func (s *stateStore) update(fn func(st *persistedState) error) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := fn(&s.st); err != nil {
		return err
	}
	return s.saveLocked()
}

// flush saves the traffic counters if they changed.
func (s *stateStore) flush() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if !s.dirty {
		return nil
	}
	return s.saveLocked()
}

func (s *stateStore) paused() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.st.Paused
}

func (s *stateStore) setPaused(p bool) error {
	err := s.update(func(st *persistedState) error { st.Paused = p; return nil })
	if p {
		s.gen.Add(1) // cut transfers in progress
	}
	return err
}

func (s *stateStore) totpSecret() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.st.TOTPSecret
}

// touch records a request from a device and returns its effective ID (after a pending rename).
func (s *stateStore) touch(id, ip string) string {
	s.mu.Lock()
	defer s.mu.Unlock()
	if newID, ok := s.st.Renames[id]; ok {
		id = newID
	} else {
		// The client sends the new ID: it adopted the rename.
		for old, n := range s.st.Renames {
			if n == id {
				delete(s.st.Renames, old)
				s.dirty = true
			}
		}
	}
	now := s.now().UnixMilli()
	d := s.st.Devices[id]
	if d == nil {
		d = &Device{FirstSeen: now, ResetAt: now}
		s.st.Devices[id] = d
		s.dirty = true
	}
	if d.ips == nil {
		d.ips = map[string]int64{}
	}
	d.ips[ip] = now
	for k, t := range d.ips {
		if now-t > onlineWindow.Milliseconds() {
			delete(d.ips, k)
		}
	}
	if d.LastIP != ip {
		d.LastIP = ip
		s.dirty = true
	}
	d.LastSeen = now
	return id
}

func (s *stateStore) addTraffic(id string, up, down int64) {
	s.mu.Lock()
	defer s.mu.Unlock()
	d := s.st.Devices[id]
	if d == nil {
		return // deleted from the panel meanwhile
	}
	d.BytesUp += up
	d.BytesDown += down
	d.speed.add(s.now().Unix(), up, down)
	s.dirty = true
}

// DeviceStatus is a device as shown in the panel.
type DeviceStatus struct {
	ID        string   `json:"id"`
	FirstSeen int64    `json:"firstSeen"`
	LastSeen  int64    `json:"lastSeen"`
	LastIP    string   `json:"lastIP"`
	IPs       []string `json:"ips"` // seen within the online window
	Online    bool     `json:"online"`
	Conflict  bool     `json:"conflict"`          // the same ID used from several IPs at once
	Pending   []string `json:"pending,omitempty"` // old IDs renamed to this one, not yet adopted by the client
	BytesUp   int64    `json:"bytesUp"`
	BytesDown int64    `json:"bytesDown"`
	ResetAt   int64    `json:"resetAt"`
	UpRate    int64    `json:"upRate"` // bytes/s
	DownRate  int64    `json:"downRate"`
}

func (s *stateStore) devices() []DeviceStatus {
	s.mu.Lock()
	defer s.mu.Unlock()
	now := s.now()
	nowMs := now.UnixMilli()
	list := []DeviceStatus{}
	for id, d := range s.st.Devices {
		ds := DeviceStatus{
			ID: id, FirstSeen: d.FirstSeen, LastSeen: d.LastSeen, LastIP: d.LastIP,
			BytesUp: d.BytesUp, BytesDown: d.BytesDown, ResetAt: d.ResetAt, IPs: []string{},
			Online: nowMs-d.LastSeen <= onlineWindow.Milliseconds(),
		}
		for ip, t := range d.ips {
			if nowMs-t <= onlineWindow.Milliseconds() {
				ds.IPs = append(ds.IPs, ip)
			}
		}
		sort.Strings(ds.IPs)
		ds.Conflict = len(ds.IPs) > 1
		for old, n := range s.st.Renames {
			if n == id {
				ds.Pending = append(ds.Pending, old)
			}
		}
		sort.Strings(ds.Pending)
		ds.UpRate, ds.DownRate = d.speed.rates(now.Unix())
		list = append(list, ds)
	}
	sort.Slice(list, func(i, j int) bool { return list[i].ID < list[j].ID })
	return list
}

var (
	errNoDevice  = errors.New("device not found")
	errIDTaken   = errors.New("equipment id already in use")
	errBadDevice = errors.New("invalid equipment id")

	// errReadOnlyElsewhere: unsetting a folder that stays read-only through another entry, e.g. "Docs"
	// for "docs" (enforcement ignores case) or a read-only ancestor.
	errReadOnlyElsewhere = errors.New("still read-only through another folder")
)

// resetTraffic zeroes the counters of one device, or of all when id is "".
func (s *stateStore) resetTraffic(id string) error {
	return s.update(func(st *persistedState) error {
		now := s.now().UnixMilli()
		for k, d := range st.Devices {
			if id == "" || k == id {
				d.BytesUp, d.BytesDown, d.ResetAt = 0, 0, now
				d.speed = speedMeter{}
			}
		}
		if id != "" && st.Devices[id] == nil {
			return errNoDevice
		}
		return nil
	})
}

func (s *stateStore) deleteDevice(id string) error {
	return s.update(func(st *persistedState) error {
		if st.Devices[id] == nil {
			return errNoDevice
		}
		delete(st.Devices, id)
		for old, n := range st.Renames {
			if n == id {
				delete(st.Renames, old)
			}
		}
		return nil
	})
}

// renameDevice moves a device's record to newID; the client picks the new ID up on its next ping.
func (s *stateStore) renameDevice(id, newID string) error {
	newID = strings.TrimSpace(newID)
	if !validEquipmentID(newID) {
		return errBadDevice
	}
	return s.update(func(st *persistedState) error {
		d := st.Devices[id]
		if d == nil {
			return errNoDevice
		}
		if newID == id {
			return nil
		}
		if st.Devices[newID] != nil {
			return errIDTaken
		}
		// newID may still be sent by a machine renamed away from it; only renaming back to it is fine.
		if target, pending := st.Renames[newID]; pending && target != id {
			return errIDTaken
		}
		delete(st.Devices, id)
		st.Devices[newID] = d
		for old, n := range st.Renames {
			if n == id {
				st.Renames[old] = newID // no chains: old IDs map straight to the newest one
			}
		}
		delete(st.Renames, newID) // renaming back to a pending old ID
		st.Renames[id] = newID
		return nil
	})
}

var reservedName = regexp.MustCompile(`(?i)^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$`)

// validEquipmentID: the client also uses the ID as a Windows file name (its backup archive),
// so the same rules as its settings window apply.
func validEquipmentID(id string) bool {
	if id == "" || utf8.RuneCountInString(id) > 64 || !utf8.ValidString(id) || strings.ContainsAny(id, `/\:*?"<>|`) ||
		reservedName.MatchString(id) {
		return false
	}
	for _, r := range id {
		if r < 0x20 || r == 0x7f {
			return false
		}
	}
	return true
}

// ---------- read-only folders ----------

// hasPathPrefix reports whether rel is dir or inside it, ignoring case.
func hasPathPrefix(rel, dir string) bool { return pathPrefix(rel, dir, true) }

func pathPrefix(rel, dir string, fold bool) bool {
	if fold {
		rel, dir = strings.ToLower(rel), strings.ToLower(dir)
	}
	return rel == dir || strings.HasPrefix(rel, dir+"/")
}

// subPath returns what follows dir in rel, which pathPrefix matched ("" when rel is dir). It counts
// segments rather than bytes: with case folding the two may differ in length (e.g. "K" and "k").
func subPath(rel, dir string) string {
	n := strings.Count(dir, "/") + 2
	if parts := strings.SplitN(rel, "/", n); len(parts) == n {
		return parts[n-1]
	}
	return ""
}

// isReadOnly reports whether rel is a read-only folder or inside one.
func (s *stateStore) isReadOnly(rel string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, ro := range s.st.ReadOnlyFolders {
		if hasPathPrefix(rel, ro) {
			return true
		}
	}
	return false
}

// readOnlyMatcher is isReadOnly for many paths at once (the tree): the folders are copied and
// lowercased once, so the checks take no lock.
func (s *stateStore) readOnlyMatcher() func(rel string) bool {
	s.mu.Lock()
	dirs := make([]string, len(s.st.ReadOnlyFolders))
	for i, ro := range s.st.ReadOnlyFolders {
		dirs[i] = strings.ToLower(ro)
	}
	s.mu.Unlock()
	return func(rel string) bool {
		if len(dirs) == 0 {
			return false
		}
		rel = strings.ToLower(rel)
		for _, d := range dirs {
			if pathPrefix(rel, d, false) {
				return true
			}
		}
		return false
	}
}

// touchesReadOnly also covers rel being an ancestor of a read-only folder,
// which deleting or moving rel would take along.
func (s *stateStore) touchesReadOnly(rel string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, ro := range s.st.ReadOnlyFolders {
		if hasPathPrefix(rel, ro) || hasPathPrefix(ro, rel) {
			return true
		}
	}
	return false
}

func (s *stateStore) readOnlyFolders() []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]string{}, s.st.ReadOnlyFolders...)
}

func (s *stateStore) setReadOnly(rel string, ro bool) error {
	return s.update(func(st *persistedState) error {
		// Enforcement ignores case, so "Docs" and "docs" protect the same paths: unsetting a listed
		// folder takes its case variants along, or neither could ever be unset.
		fold := s.foldCase || !ro && slices.Contains(st.ReadOnlyFolders, rel)
		list := st.ReadOnlyFolders[:0:0]
		for _, f := range st.ReadOnlyFolders {
			if f != rel && !(fold && strings.EqualFold(f, rel)) {
				list = append(list, f)
			}
		}
		if ro {
			list = append(list, rel)
			sort.Strings(list)
		} else if slices.ContainsFunc(list, func(f string) bool { return hasPathPrefix(rel, f) }) {
			return errReadOnlyElsewhere
		}
		st.ReadOnlyFolders = list
		return nil
	})
}

// movedPath follows read-only folders when an ancestor (or the folder itself) is moved from -> to;
// with to == "" they are dropped (the folder was deleted).
func (s *stateStore) movedPath(from, to string) error {
	return s.update(func(st *persistedState) error {
		list := st.ReadOnlyFolders[:0:0]
		for _, f := range st.ReadOnlyFolders {
			if pathPrefix(f, from, s.foldCase) {
				if to == "" {
					continue
				}
				if sub := subPath(f, from); sub != "" {
					f = to + "/" + sub
				} else {
					f = to
				}
			}
			list = append(list, f)
		}
		sort.Strings(list)
		st.ReadOnlyFolders = list
		return nil
	})
}

// addReadOnly marks several folders read-only in one save and returns the ones that were not
// already, so a failed operation can take back exactly those.
func (s *stateStore) addReadOnly(rels []string) ([]string, error) {
	if len(rels) == 0 {
		return nil, nil
	}
	var added []string
	err := s.update(func(st *persistedState) error {
		for _, rel := range rels {
			if !slices.ContainsFunc(st.ReadOnlyFolders, func(f string) bool {
				return f == rel || s.foldCase && strings.EqualFold(f, rel)
			}) {
				st.ReadOnlyFolders = append(st.ReadOnlyFolders, rel)
				added = append(added, rel)
			}
		}
		sort.Strings(st.ReadOnlyFolders)
		return nil
	})
	return added, err
}

// removeReadOnly undoes addReadOnly.
func (s *stateStore) removeReadOnly(rels []string) error {
	if len(rels) == 0 {
		return nil
	}
	return s.update(func(st *persistedState) error {
		st.ReadOnlyFolders = slices.DeleteFunc(st.ReadOnlyFolders, func(f string) bool { return slices.Contains(rels, f) })
		return nil
	})
}

// readOnlyUnder returns the read-only folders at or under rel, relative to rel ("" for rel itself),
// so they can be kept with the item in the recycle bin.
func (s *stateStore) readOnlyUnder(rel string) []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	var out []string
	for _, f := range s.st.ReadOnlyFolders {
		if pathPrefix(f, rel, s.foldCase) {
			out = append(out, subPath(f, rel))
		}
	}
	return out
}

func statePath(root string) string { return filepath.Join(root, internalDirName, "state.json") }
