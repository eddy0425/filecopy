package main

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"log"
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
	// Read-only folders at or under Path when deleted, relative to it ("" = the item itself);
	// set again on restore.
	ReadOnly []string `json:"readOnly,omitempty"`
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
func (t *trashStore) put(abs, rel, by string, readOnly []string) error {
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
	item := TrashItem{ID: id, Path: rel, IsDir: info.IsDir(), Size: treeSize(abs), DeletedAt: t.now().UnixMilli(), DeletedBy: by, ReadOnly: readOnly}
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
	if _, err := os.Lstat(filepath.Join(dir, "item")); err != nil {
		return it, errTrashID // restored already, only the cleanup failed: purge removes the leftover
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
	if err := renameNoReplace(filepath.Join(dir, "item"), abs); err != nil {
		return err
	}
	// The item is back: a leftover entry must not make the caller treat the restore as failed.
	if err := os.RemoveAll(dir); err != nil {
		log.Printf("trash: remove %s after restore: %v", dir, err)
	}
	return nil
}

// renameNoReplace moves src to dst, failing with fs.ErrExist when dst appeared after the caller
// checked it (a client upload): os.Rename would silently replace that file. A file is hard-linked
// and then unlinked, which fails if dst exists; filesystems without hard links fall back to a
// rename. A directory is renamed: that fails onto anything but an empty directory.
func renameNoReplace(src, dst string) error {
	if info, err := os.Lstat(src); err == nil && info.Mode().IsRegular() {
		err := os.Link(src, dst)
		if err == nil {
			os.Remove(src) // the trash entry is removed afterwards anyway
			return nil
		}
		if errors.Is(err, fs.ErrExist) {
			return err
		}
	}
	return os.Rename(src, dst)
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
		} else if err != nil { // no readable meta or no item: fall back to the directory's age
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

// restoredName returns a free "dir/name (restored 20260102-150405).ext", adding " 2", " 3", ...
// when several items are restored to the same place within a second.
func restoredName(abs string, now time.Time) string {
	ext := filepath.Ext(abs)
	stem := strings.TrimSuffix(abs, ext) + " (restored " + now.Format("20060102-150405")
	name := stem + ")" + ext
	for i := 2; ; i++ {
		if _, err := os.Lstat(name); err != nil { // free, or an error the rename will report
			return name
		}
		name = fmt.Sprintf("%s %d)%s", stem, i, ext)
	}
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
