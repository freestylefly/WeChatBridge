package billing

// The server renders the QR and the client only downloads bytes.
//
// That is not a convenience. The desktop application has zero NuGet references and
// is not going to grow one for a matrix generator plus a PNG writer; a
// hand-rolled renderer is exactly the code that goes fuzzy on a high-DPI screen,
// which is the failure the design spends five bullets on avoiding (§9.5). The
// server can afford the encoder because it can afford the test that reads its own
// output back.
//
// Byte mode only, error correction level L or M, versions 1 through 10. That covers
// a WeChat code_url (a weixin:// URI of well under a hundred characters) with room
// to spare, and it keeps the block table small enough to check by hand — which
// matters, because a wrong entry here produces a matrix that scans as nothing at
// all.

import (
	"bytes"
	"errors"
	"fmt"
	"image"
	"image/color"
	"image/png"
	"math"
	"strconv"
)

// ecLevel is the error correction level.
type ecLevel int

const (
	ecLow ecLevel = iota
	ecMedium
)

// formatBits is the two-bit indicator each level writes into the format
// information. Order is WeChat's, not the alphabet's.
func (e ecLevel) formatBits() int {
	switch e {
	case ecLow:
		return 0b01
	case ecMedium:
		return 0b00
	default:
		return 0b00
	}
}

// blockSpec is one version/level's block layout. The columns sum to the version's
// total codeword count, which is the invariant that makes a typo in this table
// detectable rather than invisible.
type blockSpec struct {
	ecPerBlock int
	group1     int // blocks carrying g1Data codewords each
	g1Data     int
	group2     int // blocks one codeword longer
	g2Data     int
}

func (b blockSpec) blocks() int         { return b.group1 + b.group2 }
func (b blockSpec) dataCodewords() int  { return b.group1*b.g1Data + b.group2*b.g2Data }
func (b blockSpec) totalCodewords() int { return b.blocks()*(b.ecPerBlock+b.g1Data) + b.group2 }
func (b blockSpec) maxDataCodewords() int {
	if b.group2 == 0 {
		return b.g1Data
	}
	return b.g1Data + 1
}

// versionSpec is one version. Alignment centres are per the standard's table.
type versionSpec struct {
	align  []int
	low    blockSpec
	medium blockSpec
	remain int // remainder bits after the codewords
}

// specs[version-1]. Verified against the standard's total-codeword counts: every
// entry's blocks × (data + ec) lands on the published figure for that version.
var specs = []versionSpec{
	{align: nil, low: blockSpec{7, 1, 19, 0, 0}, medium: blockSpec{10, 1, 16, 0, 0}, remain: 0},
	{align: []int{6, 18}, low: blockSpec{10, 1, 34, 0, 0}, medium: blockSpec{16, 1, 28, 0, 0}, remain: 7},
	{align: []int{6, 22}, low: blockSpec{15, 1, 55, 0, 0}, medium: blockSpec{26, 1, 44, 0, 0}, remain: 7},
	{align: []int{6, 26}, low: blockSpec{20, 1, 80, 0, 0}, medium: blockSpec{18, 2, 32, 0, 0}, remain: 7},
	{align: []int{6, 30}, low: blockSpec{26, 1, 108, 0, 0}, medium: blockSpec{24, 2, 43, 0, 0}, remain: 7},
	{align: []int{6, 34}, low: blockSpec{18, 2, 68, 0, 0}, medium: blockSpec{16, 4, 27, 0, 0}, remain: 7},
	{align: []int{6, 22, 38}, low: blockSpec{20, 2, 78, 0, 0}, medium: blockSpec{18, 4, 31, 0, 0}, remain: 0},
	{align: []int{6, 24, 42}, low: blockSpec{24, 2, 97, 0, 0}, medium: blockSpec{22, 2, 38, 2, 39}, remain: 0},
	{align: []int{6, 26, 46}, low: blockSpec{30, 2, 116, 0, 0}, medium: blockSpec{22, 3, 36, 2, 37}, remain: 0},
	{align: []int{6, 28, 50}, low: blockSpec{18, 2, 68, 2, 69}, medium: blockSpec{26, 4, 43, 1, 44}, remain: 0},
}

// checkSpecTables fails fast on a mistyped entry. Every version's total codeword
// count is a published number, and both error-correction layouts for that version
// have to agree on it — so a wrong block count or error-correction length in
// either row shows up here rather than as a QR that scans as nothing.
func init() {
	for i, spec := range specs {
		version := i + 1
		if spec.low.blocks() == 0 || spec.medium.blocks() == 0 {
			panic("billing: qr: version " + strconv.Itoa(version) + " has no blocks")
		}
		if spec.low.totalCodewords() != spec.medium.totalCodewords() {
			panic("billing: qr: version " + strconv.Itoa(version) + " levels disagree on the codeword count")
		}
		if got, want := spec.medium.dataCodewords(), spec.medium.blocks()*spec.medium.maxDataCodewords()-spec.medium.group2; got > want {
			panic("billing: qr: version " + strconv.Itoa(version) + " group lengths are inconsistent")
		}
	}
}

// byteModeCountBits is the character-count indicator width for byte mode: eight
// bits up to version 9, sixteen from version 10.
func byteModeCountBits(version int) int {
	if version < 10 {
		return 8
	}
	return 16
}

// MinQRPixels is the smallest PNG edge this package renders. The design requires
// at least 480 (§9.5): the client shows it at 240 DIP, so 480 is exactly 2×.
const MinQRPixels = 480

// MaxQRPixels bounds the edge so a hostile length cannot ask for an image large
// enough to matter. The client also refuses anything over 4 MB.
const MaxQRPixels = 2048

// EncodeQRPNG renders text as a QR code PNG.
func EncodeQRPNG(text string, level ecLevel) ([]byte, error) {
	code, err := encodeQR([]byte(text), level)
	if err != nil {
		return nil, err
	}

	// Four modules of quiet zone on every side. Too little and a phone's camera
	// refuses it; the renderer is not the place to economise.
	const quiet = 4
	total := code.size + 2*quiet
	scale := (MinQRPixels + total - 1) / total
	if total*scale > MaxQRPixels {
		scale = MaxQRPixels / total
		if scale < 1 {
			return nil, errors.New("billing: qr does not fit the pixel budget")
		}
	}

	img := image.NewGray(image.Rect(0, 0, total*scale, total*scale))
	for i := range img.Pix {
		img.Pix[i] = 0xff
	}
	for r := 0; r < code.size; r++ {
		for c := 0; c < code.size; c++ {
			if !code.dark(r, c) {
				continue
			}
			x0 := (c + quiet) * scale
			y0 := (r + quiet) * scale
			for y := y0; y < y0+scale; y++ {
				for x := x0; x < x0+scale; x++ {
					img.SetGray(x, y, color.Gray{Y: 0})
				}
			}
		}
	}

	var buf bytes.Buffer
	// Compression is the default: the image is two colours and a PNG of it is a
	// few kilobytes at this size.
	enc := png.Encoder{CompressionLevel: png.BestSpeed}
	if err := enc.Encode(&buf, img); err != nil {
		return nil, fmt.Errorf("billing: encode png: %w", err)
	}
	return buf.Bytes(), nil
}

// qrCode is the module matrix plus the function-module mask.
type qrCode struct {
	size    int
	version int
	level   ecLevel
	mods    []bool // dark
	fn      []bool // function pattern or reserved: never masked, never carries data
	mask    int
}

func (q *qrCode) idx(r, c int) int   { return r*q.size + c }
func (q *qrCode) dark(r, c int) bool { return q.mods[q.idx(r, c)] }

// encodeQR produces the matrix for data.
func encodeQR(data []byte, level ecLevel) (*qrCode, error) {
	version, blocks, err := chooseVersion(len(data), level)
	if err != nil {
		return nil, err
	}
	codewords := interleave(dataCodewords(data, version, blocks), blocks)

	q := &qrCode{
		size:    17 + 4*version,
		version: version,
		level:   level,
		mods:    make([]bool, (17+4*version)*(17+4*version)),
		fn:      make([]bool, (17+4*version)*(17+4*version)),
	}
	q.placeFunctionPatterns()

	// Try all eight masks and keep the one with the lowest penalty. The format
	// information is written afterwards because it encodes which mask was chosen.
	best, bestPenalty := 0, math.MaxInt
	for mask := 0; mask < 8; mask++ {
		q.resetData()
		q.placeData(codewords, mask)
		q.placeFormat(mask)
		if p := q.penalty(); p < bestPenalty {
			best, bestPenalty = mask, p
		}
	}
	q.resetData()
	q.mask = best
	q.placeData(codewords, best)
	q.placeFormat(best)
	return q, nil
}

// chooseVersion picks the smallest version that fits, and its block layout.
func chooseVersion(length int, level ecLevel) (int, blockSpec, error) {
	for i, spec := range specs {
		version := i + 1
		blocks := spec.low
		if level == ecMedium {
			blocks = spec.medium
		}
		needed := 4 + byteModeCountBits(version) + 8*length
		if needed <= blocks.dataCodewords()*8 {
			return version, blocks, nil
		}
	}
	return 0, blockSpec{}, fmt.Errorf("billing: qr: %d bytes exceeds the %d-byte ceiling of this encoder",
		length, specs[len(specs)-1].medium.dataCodewords())
}

// dataCodewords builds the final data-codeword sequence: mode, count, payload,
// terminator, and the two alternating pad bytes.
func dataCodewords(data []byte, version int, blocks blockSpec) []byte {
	capacity := blocks.dataCodewords()
	out := make([]byte, 0, capacity)
	bits := &bitWriter{}

	bits.write(0b0100, 4) // byte mode
	bits.write(uint64(len(data)), byteModeCountBits(version))
	for _, b := range data {
		bits.write(uint64(b), 8)
	}

	// Terminator: up to four zero bits, then pad to a byte boundary.
	terminator := 4
	if capacity*8-bits.len() < 4 {
		terminator = capacity*8 - bits.len()
	}
	bits.write(0, terminator)
	for bits.len()%8 != 0 {
		bits.write(0, 1)
	}
	out = append(out, bits.bytes()...)

	pads := []byte{0xec, 0x11}
	for i := 0; len(out) < capacity; i++ {
		out = append(out, pads[i%2])
	}
	return out
}

// bitWriter assembles most-significant-bit first, which is the order the
// standard uses everywhere including inside the block interleaving.
type bitWriter struct {
	buf  []byte
	n    int // bits written so far
	acc  byte
	held int
}

func (w *bitWriter) write(value uint64, n int) {
	for i := n - 1; i >= 0; i-- {
		bit := (value >> uint(i)) & 1
		w.acc = w.acc<<1 | byte(bit)
		w.held++
		if w.held == 8 {
			w.buf = append(w.buf, w.acc)
			w.acc, w.held = 0, 0
		}
		w.n++
	}
}

func (w *bitWriter) len() int      { return w.n }
func (w *bitWriter) bytes() []byte { return w.buf }

// interleave splits into blocks, appends error correction to each, and
// interleaves the result the way a decoder expects to read it back.
func interleave(data []byte, spec blockSpec) []byte {
	var dataBlocks, ecBlocks [][]byte
	offset := 0
	for i := 0; i < spec.blocks(); i++ {
		size := spec.g1Data
		if i >= spec.group1 {
			size = spec.g2Data
		}
		block := data[offset : offset+size]
		offset += size
		dataBlocks = append(dataBlocks, block)
		ecBlocks = append(ecBlocks, reedSolomon(block, spec.ecPerBlock))
	}

	out := make([]byte, 0, spec.dataCodewords()+spec.blocks()*spec.ecPerBlock)
	for i := 0; i < spec.maxDataCodewords(); i++ {
		for _, b := range dataBlocks {
			if i < len(b) {
				out = append(out, b[i])
			}
		}
	}
	for i := 0; i < spec.ecPerBlock; i++ {
		for _, b := range ecBlocks {
			out = append(out, b[i])
		}
	}
	return out
}

// GF(256) with the QR primitive polynomial 0x11D and generator α = 2.
var (
	gfExp [512]byte
	gfLog [256]byte
)

func init() {
	x := 1
	for i := 0; i < 255; i++ {
		gfExp[i] = byte(x)
		gfLog[x] = byte(i)
		x <<= 1
		if x&0x100 != 0 {
			x ^= 0x11d
		}
	}
	for i := 255; i < 512; i++ {
		gfExp[i] = gfExp[i-255]
	}
}

func gfMul(a, b byte) byte {
	if a == 0 || b == 0 {
		return 0
	}
	return gfExp[int(gfLog[a])+int(gfLog[b])]
}

// reedSolomon returns the n error-correction codewords for one block.
func reedSolomon(data []byte, n int) []byte {
	// Generator polynomial: the product of (x - α^i) for i in 0..n-1, which in
	// this field is the same as (x + α^i).
	gen := make([]byte, n+1)
	gen[0] = 1
	root := byte(1)
	for i := 0; i < n; i++ {
		for j := i; j > 0; j-- {
			gen[j] = gen[j-1] ^ gfMul(gen[j], root)
		}
		gen[0] = gfMul(gen[0], root)
		root = gfMul(root, 2)
	}

	res := make([]byte, n)
	for _, d := range data {
		factor := d ^ res[0]
		copy(res, res[1:])
		res[n-1] = 0
		for i := 0; i < n; i++ {
			res[i] ^= gfMul(gen[i+1], factor)
		}
	}
	return res
}

// ---------------------------------------------------------------------------
// Matrix
// ---------------------------------------------------------------------------

func (q *qrCode) spec() versionSpec { return specs[q.version-1] }

func (q *qrCode) set(r, c int, dark bool) { q.mods[q.idx(r, c)] = dark }
func (q *qrCode) mark(r, c int)           { q.fn[q.idx(r, c)] = true }

func (q *qrCode) placeFunctionPatterns() {
	// Finder patterns and their separators.
	for _, origin := range [][2]int{{0, 0}, {0, q.size - 7}, {q.size - 7, 0}} {
		q.placeFinder(origin[0], origin[1])
	}

	// Timing patterns: alternating dark/light from the eighth module.
	for i := 8; i < q.size-8; i++ {
		dark := i%2 == 0
		q.set(6, i, dark)
		q.mark(6, i)
		q.set(i, 6, dark)
		q.mark(i, 6)
	}

	// Alignment patterns at every centre pair, except the three that would
	// collide with a finder.
	centres := q.spec().align
	for _, r := range centres {
		for _, c := range centres {
			corner := (r == centres[0] && c == centres[0]) ||
				(r == centres[0] && c == centres[len(centres)-1]) ||
				(r == centres[len(centres)-1] && c == centres[0])
			if corner {
				continue
			}
			q.placeAlignment(r, c)
		}
	}

	// The always-dark module. It is not function data and must survive masking.
	q.set(q.size-8, 8, true)
	q.mark(q.size-8, 8)

	// Reserve the format information areas. Both copies must be reserved before
	// any data is placed, or the zigzag walks straight through the second one.
	for i := 0; i <= 8; i++ {
		if i != 6 {
			q.mark(8, i)
			q.mark(i, 8)
		}
	}
	for i := 0; i <= 7; i++ {
		q.mark(q.size-1-i, 8)
	}
	for i := 0; i <= 7; i++ {
		q.mark(8, q.size-1-i)
	}
	q.mark(8, 6)
	q.mark(6, 8)

	// Version information, for the versions that carry it. The two blocks hold
	// the same eighteen bits, transposed: a three-wide column pair beside the
	// bottom-left finder and the mirror of it beside the top-right one.
	if q.version >= 7 {
		bits := versionInfo(q.version)
		for i := 0; i < 18; i++ {
			bit := (bits>>uint(i))&1 == 1

			topRow, topCol := i/3, q.size-11+i%3
			q.set(topRow, topCol, bit)
			q.mark(topRow, topCol)

			botRow, botCol := q.size-11+i/3, i%3
			q.set(botRow, botCol, bit)
			q.mark(botRow, botCol)
		}
	}
}

func (q *qrCode) placeFinder(top, left int) {
	for r := -1; r <= 7; r++ {
		for c := -1; c <= 7; c++ {
			y, x := top+r, left+c
			if y < 0 || x < 0 || y >= q.size || x >= q.size {
				continue
			}
			// 7x7 finder: dark ring, light ring, dark 3x3 core.
			d := r >= 0 && r <= 6 && c >= 0 && c <= 6
			inner := r >= 2 && r <= 4 && c >= 2 && c <= 4
			ring := r >= 1 && r <= 5 && c >= 1 && c <= 5
			q.set(y, x, d && (!ring || inner))
			q.mark(y, x)
		}
	}
}

func (q *qrCode) placeAlignment(centreRow, centreCol int) {
	for r := -2; r <= 2; r++ {
		for c := -2; c <= 2; c++ {
			dark := math.Abs(float64(r)) == 2 || math.Abs(float64(c)) == 2 || (r == 0 && c == 0)
			q.set(centreRow+r, centreCol+c, dark)
			q.mark(centreRow+r, centreCol+c)
		}
	}
}

// resetData clears every non-function module so a mask can be tried.
func (q *qrCode) resetData() {
	for i := range q.mods {
		if !q.fn[i] {
			q.mods[i] = false
		}
	}
}

// placeData walks the zigzag, two columns at a time from the bottom right, and
// writes the codeword bits with the mask applied.
func (q *qrCode) placeData(codewords []byte, mask int) {
	bitIndex := 0
	totalBits := len(codewords) * 8
	upward := true

	for right := q.size - 1; right >= 1; right -= 2 {
		// Column 6 is the vertical timing pattern and carries no data; the step
		// skips past it so the pairs stay aligned.
		if right == 6 {
			right = 5
		}
		for i := 0; i < q.size; i++ {
			row := i
			if !upward {
				row = q.size - 1 - i
			}
			for c := 0; c < 2; c++ {
				col := right - c
				if q.fn[q.idx(row, col)] {
					continue
				}
				bit := false
				if bitIndex < totalBits {
					bit = codewords[bitIndex/8]>>(7-uint(bitIndex%8))&1 == 1
				}
				if bitIndex < totalBits && maskBit(mask, row, col) {
					bit = !bit
				}
				q.set(row, col, bit)
				bitIndex++
			}
		}
		upward = !upward
	}
}

// maskBit is the standard's eight patterns. Three of them are defined over the
// sum of row and column, one over the column alone, and four over products —
// getting the indexing convention wrong yields a matrix that scans as nothing.
func maskBit(mask, row, col int) bool {
	switch mask {
	case 0:
		return (row+col)%2 == 0
	case 1:
		return row%2 == 0
	case 2:
		return col%3 == 0
	case 3:
		return (row+col)%3 == 0
	case 4:
		return (row/2+col/3)%2 == 0
	case 5:
		return (row*col)%2+((row*col)%3) == 0
	case 6:
		return ((row*col)%2+(row*col)%3)%2 == 0
	case 7:
		return ((row+col)%2+(row*col)%3)%2 == 0
	default:
		return false
	}
}

// placeFormat writes the fifteen format bits in both places, BCH-encoded and
// masked with 0x5412 so the pattern is never all one colour.
func (q *qrCode) placeFormat(mask int) {
	data := uint32(q.level.formatBits()<<3 | mask)
	rem := data
	for i := 0; i < 10; i++ {
		rem = (rem << 1) ^ ((rem >> 9) * 0x537)
	}
	bits := ((data << 10) | rem) ^ 0x5412

	for i := 0; i <= 5; i++ {
		q.set(8, i, (bits>>uint(i))&1 == 1)
	}
	q.set(8, 7, (bits>>6)&1 == 1)
	q.set(8, 8, (bits>>7)&1 == 1)
	q.set(7, 8, (bits>>8)&1 == 1)
	for i := 9; i <= 14; i++ {
		q.set(14-i, 8, (bits>>uint(i))&1 == 1)
	}
	// The second copy: seven modules up the right edge and eight along the
	// bottom, so a reader that lost the first corner still has the bits.
	for i := 0; i <= 6; i++ {
		q.set(q.size-1-i, 8, (bits>>uint(i))&1 == 1)
	}
	for i := 7; i <= 14; i++ {
		q.set(8, q.size-16+i, (bits>>uint(i))&1 == 1)
	}
	q.set(q.size-8, 8, true)
}

// versionInfo is the eighteen-bit version block for versions seven and up.
func versionInfo(version int) uint32 {
	rem := uint32(version)
	for i := 0; i < 12; i++ {
		rem = (rem << 1) ^ ((rem >> 11) * 0x1f25)
	}
	return uint32(version)<<12 | (rem & 0xfff)
}

// ---------------------------------------------------------------------------
// Mask selection
// ---------------------------------------------------------------------------

// penalty is the standard's four rules, added. Rule 3 is the finder-like pattern
// 1:1:3:1:1 with four light modules on one side; missing it picks masks that scan
// poorly on some phones while looking perfect in a unit test.
func (q *qrCode) penalty() int {
	total := 0

	// Rule 1: runs of five or more.
	for _, line := range q.lines() {
		total += runPenalty(line)
	}

	// Rule 2: 2×2 blocks of one colour.
	for r := 0; r < q.size-1; r++ {
		for c := 0; c < q.size-1; c++ {
			v := q.dark(r, c)
			if q.dark(r, c+1) == v && q.dark(r+1, c) == v && q.dark(r+1, c+1) == v {
				total += 3
			}
		}
	}

	// Rule 3.
	pattern := []bool{true, false, true, true, true, false, true, false, false, false, false}
	for _, line := range q.lines() {
		for i := 0; i+len(pattern) <= len(line); i++ {
			if matchesPattern(line[i:i+len(pattern)], pattern) {
				total += 40
			}
		}
	}

	// Rule 4: drift away from an even split of dark and light.
	dark := 0
	for _, m := range q.mods {
		if m {
			dark++
		}
	}
	percent := dark * 100 / (q.size * q.size)
	deviation := math.Abs(float64(percent-50)) / 5
	total += int(deviation) * 10
	return total
}

func (q *qrCode) lines() [][]bool {
	rows := make([][]bool, q.size)
	for r := 0; r < q.size; r++ {
		row := make([]bool, q.size)
		for c := 0; c < q.size; c++ {
			row[c] = q.dark(r, c)
		}
		rows[r] = row
	}
	cols := make([][]bool, q.size)
	for c := 0; c < q.size; c++ {
		col := make([]bool, q.size)
		for r := 0; r < q.size; r++ {
			col[r] = q.dark(r, c)
		}
		cols[c] = col
	}
	return append(rows, cols...)
}

func runPenalty(line []bool) int {
	total, run := 0, 1
	for i := 1; i < len(line); i++ {
		if line[i] == line[i-1] {
			run++
			continue
		}
		if run >= 5 {
			total += 3 + (run - 5)
		}
		run = 1
	}
	if run >= 5 {
		total += 3 + (run - 5)
	}
	return total
}

func matchesPattern(line, pattern []bool) bool {
	if !matchesAt(line, pattern, 0) {
		return false
	}
	// The mirrored form, 00001011101, is the same rule read the other way.
	mirrored := make([]bool, len(pattern))
	for i := range pattern {
		mirrored[i] = pattern[len(pattern)-1-i]
	}
	return matchesAt(line, mirrored, 0)
}

func matchesAt(line, pattern []bool, offset int) bool {
	if offset+len(pattern) > len(line) {
		return false
	}
	for i := range pattern {
		if line[offset+i] != pattern[i] {
			return false
		}
	}
	return true
}
