/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System.Text.Json.Serialization;

namespace MediaPlayer.Models;

public class AppSettings
{
    public bool saveOnExitLastState = true;
    public bool autoSaveLastState = true;
    public bool loadLastState = true;

    [JsonIgnore]
    public bool canSave = true;
}
