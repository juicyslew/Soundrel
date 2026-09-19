# Audio test fixtures

These fixtures are generated test signals and contain no third-party music.

- `mono-22050-tone.wav` is a 0.25-second, mono, 22,050 Hz, 16-bit PCM 440 Hz sine tone generated sample-by-sample.
- `silent-12-frame.mp3` is twelve deterministic 417-byte MPEG-1 Layer III frames. Each frame starts with `FF FB 90 00`; all payload bytes are zero. It represents 128 kbps, 44,100 Hz stereo silence.

Run `powershell -ExecutionPolicy Bypass -File .\Soundrel.Tests\Fixtures\Generate-Fixtures.ps1` from the repository root to regenerate both files without external tools or downloaded content.
