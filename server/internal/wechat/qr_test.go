package wechat

import "testing"

// The client checks the magic bytes and refuses anything over 4 MB. Producing
// something inside that envelope is this side's responsibility: the alternative
// is an HTML error page arriving at a WPF decoder, which surfaces as a
// NotSupportedException deep in System.Windows and reads like a client bug.
func TestRenderedQRIsAPNGWithinTheClientLimit(t *testing.T) {
	t.Parallel()

	png, err := RenderQRPNG("https://open.weixin.qq.com/connect/qrconnect?appid=wx1234567890abcdef", DefaultQRSize)
	if err != nil {
		t.Fatalf("RenderQRPNG: %v", err)
	}
	if !IsPNG(png) {
		t.Fatalf("output does not start with the PNG signature: % x", png[:min(8, len(png))])
	}
	if len(png) > MaxQRBytes {
		t.Fatalf("output is %d bytes, over the client's %d byte limit", len(png), MaxQRBytes)
	}
	// The client decodes down to 240 logical pixels. A 480 pixel PNG is a few
	// kilobytes; anything in the megabytes means the encoder did something
	// nobody asked for.
	if len(png) > 200*1024 {
		t.Fatalf("output is %d bytes for a single QR code, which is far more than it should be", len(png))
	}
}

func TestRenderedQRDoesNotFetchItsOwnContent(t *testing.T) {
	t.Parallel()

	// qrcode.Encode fetches a URL it is given. The authorize URL is a URL, so
	// an Encode-based implementation would make the server ask WeChat's
	// connect page for itself on every single login.
	//
	// This asserts the observable part: a well-formed but unresolvable host
	// still renders, which it only can if nothing was fetched.
	png, err := RenderQRPNG("https://a-host-that-does-not-resolve.invalid/connect?appid=wx&state=t", 256)
	if err != nil {
		t.Fatalf("RenderQRPNG attempted to resolve or fetch the URL: %v", err)
	}
	if !IsPNG(png) {
		t.Fatal("expected a PNG")
	}
}

func TestRenderQRRejectsAnEmptyBody(t *testing.T) {
	t.Parallel()
	if _, err := RenderQRPNG("", 0); err == nil {
		t.Fatal("an empty body must be an error, not a blank square")
	}
}

func TestRenderQRClampsAnAbsurdSize(t *testing.T) {
	t.Parallel()

	// A caller asking for a 100000 pixel square would spend a very large
	// amount of memory producing a picture nobody will look at closely. The
	// clamp is what keeps the 4 MB ceiling reachable rather than theoretical.
	png, err := RenderQRPNG("https://example.com/qr", 100000)
	if err != nil {
		t.Fatalf("RenderQRPNG: %v", err)
	}
	if len(png) > MaxQRBytes {
		t.Fatalf("clamped output is still %d bytes", len(png))
	}
}

func TestIsPNGRejectsNonImages(t *testing.T) {
	t.Parallel()

	for _, b := range [][]byte{
		nil,
		[]byte("<html>error</html>"),
		{0x89, 'P', 'N', 'G'},
	} {
		if IsPNG(b) {
			t.Errorf("% x should not pass the PNG check", b)
		}
	}
}
