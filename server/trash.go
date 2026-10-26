package main

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

const trashRetention = 30 * 24 * time.Hour

// TrashItem is one deleted file or folder. It lives in .filecopy/trash/<id>/item,
// next to its meta.json; same filesystem as the data, so deleting and restoring are renames.
type TrashItem struct {
	ID        string `json:"id"`
	Path      string `json:"path"` // original path
	IsDir     bool   `json:"isDir"`
	Size      int64  `json:"size"`
	DeletedAt int64  `json:"deletedAt"` // unix ms
	DeletedBy string `json:"deletedBy"` // EquipmentId, or the panel
}

type trashStore struct {
	dir string
	now func() time.Time
}

var errTrashID = errors.New("trash item not found")

func (t *trashStore) itemDir(id string) (string, error) {
	if _, err := hex.DecodeString(id); err != nil || len(id) != 24 {
		return "", errTrashID
	}
	return filepath.Join(t.dir, id), nil
}

// put moves abs (at rel) into the recycle bin.
func (t *trashStore) put(abs, rel, by string) error {
	info, err := os.Lstat(abs)
	if err != nil {
		return err
	}
	b := make([]byte, 12)
	rand.Read(b)
	id := hex.EncodeToString(b)
	dir := filepath.Join(t.dir, id)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	item := TrashItem{ID: id, Path: rel, IsDir: info.IsDir(), Size: treeSize(abs), DeletedAt: t.now().UnixMilli(), DeletedBy: by}
	meta, _ := json.Marshal(item)
	if err := os.WriteFile(filepath.Join(dir, "meta.json"), meta, 0o644); err != nil {
		os.RemoveAll(dir)
		return err
	}
	if err := os.Rename(abs, filepath.Join(dir, "item")); err != nil {
		os.RemoveAll(dir)
		return err
	}
	return nil
}

func (t *trashStore) list() []TrashItem {
	items := []TrashItem{}
	dirs, _ := os.ReadDir(t.dir)
	for _, d := range dirs {
		if it, err := t.get(d.Name()); err == nil {
			items = append(items, it)
		}
	}
	sort.Slice(items, func(i, j int) bool { return items[i].DeletedAt > items[j].DeletedAt })
	return items
}

func (t *trashStore) get(id string) (TrashItem, error) {
	var it TrashItem
	dir, err := t.itemDir(id)
	if err != nil {
		return it, err
	}
	data, err := os.ReadFile(filepath.Join(dir, "meta.json"))
	if err != nil {
		return it, errTrashID
	}
	if err := json.Unmarshal(data, &it); err != nil {
		return it, errTrashID
	}
	it.ID = id
	return it, nil
}

// restore moves an item back to abs (its original location, or an alternative when that is taken).
func (t *trashStore) restore(id, abs string) error {
	dir, err := t.itemDir(id)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(abs), 0o755); err != nil {
		return err
	}
	if err := os.Rename(filepath.Join(dir, "item"), abs); err != nil {
		return err
	}
	return os.RemoveAll(dir)
}

func (t *trashStore) remove(id string) error {
	dir, err := t.itemDir(id)
	if err != nil {
		return err
	}
	if _, err := os.Stat(dir); err != nil {
		return errTrashID
	}
	return os.RemoveAll(dir)
}

func (t *trashStore) empty() error {
	dirs, err := os.ReadDir(t.dir)
	if err != nil {
		return err
	}
	for _, d := range dirs {
		if err := os.RemoveAll(filepath.Join(t.dir, d.Name())); err != nil {
			return err
		}
	}
	return nil
}

// purge permanently deletes items older than the retention period.
func (t *trashStore) purge() {
	cutoff := t.now().Add(-trashRetention)
	dirs, _ := os.ReadDir(t.dir)
	for _, d := range dirs {
		it, err := t.get(d.Name())
		if err == nil && it.DeletedAt < cutoff.UnixMilli() {
			os.RemoveAll(filepath.Join(t.dir, d.Name()))
		} else if err != nil { // no readable meta: fall back to the directory's age
			if info, err := d.Info(); err == nil && info.ModTime().Before(cutoff) {
				os.RemoveAll(filepath.Join(t.dir, d.Name()))
			}
		}
	}
}

func (t *trashStore) usage() int64 {
	var n int64
	for _, it := range t.list() {
		n += it.Size
	}
	return n
}

// restoredName returns "dir/name (restored 20260102-150405).ext".
func restoredName(abs string, now time.Time) string {
	ext := filepath.Ext(abs)
	base := strings.TrimSuffix(abs, ext)
	return base + " (restored " + now.Format("20060102-150405") + ")" + ext
}

// treeSize is the total size of the regular files at or under abs.
func treeSize(abs string) int64 {
	var n int64
	filepath.WalkDir(abs, func(_ string, d fs.DirEntry, err error) error {
		if err == nil && d.Type().IsRegular() {
			if info, err := d.Info(); err == nil {
				n += info.Size()
			}
		}
		return nil
	})
	return n
}
