$ErrorActionPreference = 'Stop'

$fixtureDirectory = $PSScriptRoot

$sampleRate = 22050
$channelCount = 1
$bitsPerSample = 16
$sampleCount = [int]($sampleRate / 4)
$dataLength = $sampleCount * $channelCount * ($bitsPerSample / 8)
$wavPath = Join-Path $fixtureDirectory 'mono-22050-tone.wav'

$stream = [System.IO.File]::Create($wavPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([System.Text.Encoding]::ASCII.GetBytes('RIFF'))
    $writer.Write([int](36 + $dataLength))
    $writer.Write([System.Text.Encoding]::ASCII.GetBytes('WAVE'))
    $writer.Write([System.Text.Encoding]::ASCII.GetBytes('fmt '))
    $writer.Write([int]16)
    $writer.Write([int16]1)
    $writer.Write([int16]$channelCount)
    $writer.Write([int]$sampleRate)
    $writer.Write([int]($sampleRate * $channelCount * ($bitsPerSample / 8)))
    $writer.Write([int16]($channelCount * ($bitsPerSample / 8)))
    $writer.Write([int16]$bitsPerSample)
    $writer.Write([System.Text.Encoding]::ASCII.GetBytes('data'))
    $writer.Write([int]$dataLength)

    for ($sample = 0; $sample -lt $sampleCount; $sample++) {
        $value = [int16]([Math]::Round(
            [Math]::Sin(2 * [Math]::PI * 440 * $sample / $sampleRate) * 6553))
        $writer.Write($value)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

$frameCount = 12
$frameLength = 417
$mp3Bytes = [byte[]]::new($frameCount * $frameLength)
for ($frame = 0; $frame -lt $frameCount; $frame++) {
    $offset = $frame * $frameLength
    $mp3Bytes[$offset] = 0xFF
    $mp3Bytes[$offset + 1] = 0xFB
    $mp3Bytes[$offset + 2] = 0x90
    $mp3Bytes[$offset + 3] = 0x00
}

[System.IO.File]::WriteAllBytes(
    (Join-Path $fixtureDirectory 'silent-12-frame.mp3'),
    $mp3Bytes)
