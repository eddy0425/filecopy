package main

import (
	"errors"
	"fmt"
	"strings"
)

// Minimal QR code encoder for the TOTP setup URI: byte mode, error correction
// level M, versions 1-10. Follows ISO/IEC 18004; structure after Nayuki's
// reference implementation.

// qrBlocks describes level M error correction per version (index 1..10):
// EC codewords per block, then (block count, data codewords) for both groups.
var qrBlocks = [11]struct{ ec, n1, d1, n2, d2 int }{
	{},
	{10, 1, 16, 0, 0},
	{16, 1, 28, 0, 0},
	{26, 1, 44, 0, 0},
	{18, 2, 32, 0, 0},
	{24, 2, 43, 0, 0},
	{16, 4, 27, 0, 0},
	{18, 4, 31, 0, 0},
	{22, 2, 38, 2, 39},
	{22, 3, 36, 2, 37},
	{26, 4, 43, 1, 44},
}

var qrAlign = [11][]int{
	nil, nil,
	{6, 18}, {6, 22}, {6, 26}, {6, 30}, {6, 34},
	{6, 22, 38}, {6, 24, 42}, {6, 26, 46}, {6, 28, 50},
}

const qrFormatM = 0 // format bits of error correction level M

type qrCode struct {
	size     int
	modules  [][]bool // [y][x], true = dark
	function [][]bool // finder/timing/alignment/format/version areas
}

func (q *qrCode) set(x, y int, dark bool) {
	q.modules[y][x] = dark
	q.function[y][x] = true
}

// encodeQR encodes data as a QR code at the smallest version that fits.
func encodeQR(data []byte) (*qrCode, error) {
	for ver := 1; ver <= 10; ver++ {
		b := qrBlocks[ver]
		capacity := b.n1*b.d1 + b.n2*b.d2
		countBits := 8
		if ver >= 10 {
			countBits = 16
		}
		if 4+countBits+8*len(data) > capacity*8 {
			continue
		}
		return buildQR(ver, qrDataCodewords(data, countBits, capacity)), nil
	}
	return nil, errors.New("qr: data too long")
}

// qrDataCodewords builds the byte-mode bit stream, padded to capacity bytes.
func qrDataCodewords(data []byte, countBits, capacity int) []byte {
	var bits []bool
	put := func(v, n int) {
		for i := n - 1; i >= 0; i-- {
			bits = append(bits, (v>>i)&1 == 1)
		}
	}
	put(0b0100, 4) // byte mode
	put(len(data), countBits)
	for _, c := range data {
		put(int(c), 8)
	}
	put(0, min(4, capacity*8-len(bits))) // terminator
	for len(bits)%8 != 0 {
		bits = append(bits, false)
	}
	out := make([]byte, 0, capacity)
	for i := 0; i < len(bits); i += 8 {
		var c byte
		for j := 0; j < 8; j++ {
			if bits[i+j] {
				c |= 1 << (7 - j)
			}
		}
		out = append(out, c)
	}
	for pad := byte(0xEC); len(out) < capacity; pad ^= 0xEC ^ 0x11 {
		out = append(out, pad)
	}
	return out
}

func buildQR(ver int, data []byte) *qrCode {
	size := ver*4 + 17
	q := &qrCode{size: size, modules: make([][]bool, size), function: make([][]bool, size)}
	for i := range q.modules {
		q.modules[i] = make([]bool, size)
		q.function[i] = make([]bool, size)
	}
	q.drawFunctionPatterns(ver)
	q.drawCodewords(qrInterleave(ver, data))

	best, bestPenalty := 0, -1
	for mask := 0; mask < 8; mask++ {
		q.applyMask(mask)
		q.drawFormat(mask)
		if p := q.penalty(); bestPenalty < 0 || p < bestPenalty {
			best, bestPenalty = mask, p
		}
		q.applyMask(mask) // XOR again to undo
	}
	q.applyMask(best)
	q.drawFormat(best)
	return q
}

func (q *qrCode) drawFunctionPatterns(ver int) {
	for i := 0; i < q.size; i++ {
		q.set(6, i, i%2 == 0)
		q.set(i, 6, i%2 == 0)
	}
	for _, c := range [][2]int{{3, 3}, {q.size - 4, 3}, {3, q.size - 4}} {
		for dy := -4; dy <= 4; dy++ {
			for dx := -4; dx <= 4; dx++ {
				x, y := c[0]+dx, c[1]+dy
				if x >= 0 && x < q.size && y >= 0 && y < q.size {
					d := max(abs(dx), abs(dy))
					q.set(x, y, d != 2 && d != 4)
				}
			}
		}
	}
	pos := qrAlign[ver]
	for i, ay := range pos {
		for j, ax := range pos {
			last := len(pos) - 1
			if i == 0 && j == 0 || i == 0 && j == last || i == last && j == 0 {
				continue // overlaps a finder pattern
			}
			for dy := -2; dy <= 2; dy++ {
				for dx := -2; dx <= 2; dx++ {
					q.set(ax+dx, ay+dy, max(abs(dx), abs(dy)) != 1)
				}
			}
		}
	}
	q.drawFormat(0) // reserve the area; redrawn once the mask is chosen
	if ver >= 7 {
		bits := qrVersionBits(ver)
		for i := 0; i < 18; i++ {
			dark := (bits>>i)&1 == 1
			a, b := q.size-11+i%3, i/3
			q.set(a, b, dark)
			q.set(b, a, dark)
		}
	}
}

func qrFormatBits(mask int) int {
	data := qrFormatM<<3 | mask
	rem := data
	for i := 0; i < 10; i++ {
		rem = rem<<1 ^ (rem>>9)*0x537
	}
	return (data<<10 | rem) ^ 0x5412
}

func qrVersionBits(ver int) int {
	rem := ver
	for i := 0; i < 12; i++ {
		rem = rem<<1 ^ (rem>>11)*0x1F25
	}
	return ver<<12 | rem
}

func (q *qrCode) drawFormat(mask int) {
	bits := qrFormatBits(mask)
	bit := func(i int) bool { return (bits>>i)&1 == 1 }
	for i := 0; i <= 5; i++ {
		q.set(8, i, bit(i))
	}
	q.set(8, 7, bit(6))
	q.set(8, 8, bit(7))
	q.set(7, 8, bit(8))
	for i := 9; i < 15; i++ {
		q.set(14-i, 8, bit(i))
	}
	for i := 0; i < 8; i++ {
		q.set(q.size-1-i, 8, bit(i))
	}
	for i := 8; i < 15; i++ {
		q.set(8, q.size-15+i, bit(i))
	}
	q.set(8, q.size-8, true) // always dark
}

// qrInterleave splits data into blocks, appends Reed-Solomon codewords and interleaves them.
func qrInterleave(ver int, data []byte) []byte {
	b := qrBlocks[ver]
	var blocks, ecs [][]byte
	off := 0
	for i := 0; i < b.n1+b.n2; i++ {
		n := b.d1
		if i >= b.n1 {
			n = b.d2
		}
		blk := data[off : off+n]
		off += n
		blocks = append(blocks, blk)
		ecs = append(ecs, rsRemainder(blk, b.ec))
	}
	var out []byte
	for i := 0; i < max(b.d1, b.d2); i++ {
		for _, blk := range blocks {
			if i < len(blk) {
				out = append(out, blk[i])
			}
		}
	}
	for i := 0; i < b.ec; i++ {
		for _, ec := range ecs {
			out = append(out, ec[i])
		}
	}
	return out
}

func gfMul(x, y byte) byte {
	var z int
	for i := 7; i >= 0; i-- {
		z = z<<1 ^ (z>>7)*0x11D
		if (y>>i)&1 == 1 {
			z ^= int(x)
		}
	}
	return byte(z)
}

// rsRemainder returns the n Reed-Solomon error correction codewords for data.
func rsRemainder(data []byte, n int) []byte {
	div := make([]byte, n) // generator polynomial without its leading 1
	div[n-1] = 1
	root := byte(1)
	for i := 0; i < n; i++ {
		for j := 0; j < n; j++ {
			div[j] = gfMul(div[j], root)
			if j+1 < n {
				div[j] ^= div[j+1]
			}
		}
		root = gfMul(root, 0x02)
	}
	res := make([]byte, n)
	for _, b := range data {
		f := b ^ res[0]
		copy(res, res[1:])
		res[n-1] = 0
		for i := range res {
			res[i] ^= gfMul(div[i], f)
		}
	}
	return res
}

func (q *qrCode) drawCodewords(data []byte) {
	i := 0
	for right := q.size - 1; right >= 1; right -= 2 {
		if right == 6 {
			right = 5 // skip the vertical timing pattern
		}
		for vert := 0; vert < q.size; vert++ {
			for j := 0; j < 2; j++ {
				x := right - j
				y := vert
				if (right+1)&2 == 0 { // upward column
					y = q.size - 1 - vert
				}
				if !q.function[y][x] && i < len(data)*8 {
					q.modules[y][x] = (data[i>>3]>>(7-i&7))&1 == 1
					i++
				}
			}
		}
	}
}

func (q *qrCode) applyMask(mask int) {
	for y := 0; y < q.size; y++ {
		for x := 0; x < q.size; x++ {
			var inv bool
			switch mask {
			case 0:
				inv = (x+y)%2 == 0
			case 1:
				inv = y%2 == 0
			case 2:
				inv = x%3 == 0
			case 3:
				inv = (x+y)%3 == 0
			case 4:
				inv = (x/3+y/2)%2 == 0
			case 5:
				inv = x*y%2+x*y%3 == 0
			case 6:
				inv = (x*y%2+x*y%3)%2 == 0
			case 7:
				inv = ((x+y)%2+x*y%3)%2 == 0
			}
			if inv && !q.function[y][x] {
				q.modules[y][x] = !q.modules[y][x]
			}
		}
	}
}

// penalty scores a masked symbol by the standard rules; lower is better.
func (q *qrCode) penalty() int {
	n := q.size
	at := func(x, y int, transpose bool) bool {
		if transpose {
			return q.modules[x][y]
		}
		return q.modules[y][x]
	}
	p := 0
	finder := []bool{true, false, true, true, true, false, true}
	for _, tr := range []bool{false, true} {
		for y := 0; y < n; y++ {
			run := 1
			for x := 1; x <= n; x++ {
				if x < n && at(x, y, tr) == at(x-1, y, tr) {
					run++
					continue
				}
				if run >= 5 {
					p += 3 + run - 5
				}
				run = 1
			}
			// 1:1:3:1:1 finder-like pattern with 4 light modules on either side.
			for x := 0; x+7 <= n; x++ {
				match := true
				for k, v := range finder {
					if at(x+k, y, tr) != v {
						match = false
						break
					}
				}
				if !match {
					continue
				}
				light := func(from, to int) bool {
					for k := from; k < to; k++ {
						if k >= 0 && k < n && at(k, y, tr) {
							return false
						}
					}
					return true
				}
				if light(x-4, x) || light(x+7, x+11) {
					p += 40
				}
			}
		}
	}
	dark := 0
	for y := 0; y < n; y++ {
		for x := 0; x < n; x++ {
			if q.modules[y][x] {
				dark++
			}
			if x+1 < n && y+1 < n {
				c := q.modules[y][x]
				if c == q.modules[y][x+1] && c == q.modules[y+1][x] && c == q.modules[y+1][x+1] {
					p += 3
				}
			}
		}
	}
	total := n * n
	k := (abs(dark*20-total*10) + total - 1) / total // deviation from 50% in 5% steps
	return p + max(k-1, 0)*10
}

// svg renders the code with a 4-module quiet zone.
func (q *qrCode) svg() string {
	var sb strings.Builder
	dim := q.size + 8
	fmt.Fprintf(&sb, `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d" shape-rendering="crispEdges">`, dim, dim)
	fmt.Fprintf(&sb, `<rect width="%d" height="%d" fill="#fff"/><path fill="#000" d="`, dim, dim)
	for y := 0; y < q.size; y++ {
		for x := 0; x < q.size; x++ {
			if q.modules[y][x] {
				fmt.Fprintf(&sb, "M%d %dh1v1h-1z", x+4, y+4)
			}
		}
	}
	sb.WriteString(`"/></svg>`)
	return sb.String()
}

func abs(v int) int {
	if v < 0 {
		return -v
	}
	return v
}
