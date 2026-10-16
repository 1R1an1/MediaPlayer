using System;

namespace MediaPlayer.Models;

public enum ProbeMode
{
    OnlyCoverWithoutHash,
    OnlyCover,
    AllWithoutHash,
    All
}

public class MediaProbe
{
    public byte[] CoverBytes = null;
    public string CoverHashHex = string.Empty;
    public TimeSpan Duration;
    public string Artist = string.Empty;
}
