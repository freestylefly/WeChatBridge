package wechat

import (
	"bytes"
	"fmt"

	qrcode "github.com/skip2/go-qrcode"
)

// The client verifies these magic bytes before handing the bytes to a WPF
// decoder, and refuses anything larger. A wrong server answering an image
// request with an HTML error page otherwise surfaces as a NotSupportedException
// deep inside the image decoder, which reads like a client bug rather than a
// bad response. Producing something within the limit is this side's job.
var pngMagic = []byte{0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A}

// MaxQRBytes is the ceiling the client enforces on the PNG.
const MaxQRBytes = 4 << 20

// DefaultQRSize is the pixel width of the rendered PNG. The client decodes
// down to 240 logical pixels, so rendering larger only spends bytes.
const DefaultQRSize = 480

// RenderQRPNG encodes body into a PNG.
//
// It uses qrcode.New + png rather than qrcode.Encode on purpose: Encode will
// fetch its input over HTTP when the string looks like a URL, which for an
// authorize URL means the server would be asking WeChat's page for itself,
// adding a network round trip to every login and a failure mode nobody asked
// for.
func RenderQRPNG(body string, size int) ([]byte, error) {
	if body == "" {
		return nil, fmt.Errorf("wechat: render qr: empty body")
	}
	if size <= 0 {
		size = DefaultQRSize
	}
	// A very large size would blow past MaxQRBytes with a big enough module
	// count; the caller cannot know the module count, so the bound is applied
	// after encoding and enforced above by the size clamp.
	if size > 1024 {
		size = 1024
	}

	code, err := qrcode.New(body, qrcode.Medium)
	if err != nil {
		return nil, fmt.Errorf("wechat: render qr: %w", err)
	}
	out, err := code.PNG(size)
	if err != nil {
		return nil, fmt.Errorf("wechat: render qr: %w", err)
	}
	if len(out) > MaxQRBytes {
		return nil, fmt.Errorf("wechat: render qr: %d bytes exceeds the %d byte limit", len(out), MaxQRBytes)
	}
	return out, nil
}

// IsPNG reports whether b starts with the PNG signature. It is the same check
// the client makes, kept here so a test can assert the two agree.
func IsPNG(b []byte) bool {
	return len(b) >= len(pngMagic) && bytes.Equal(b[:len(pngMagic)], pngMagic)
}
