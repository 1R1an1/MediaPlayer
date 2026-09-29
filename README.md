# MediaPlayer

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
![Platform](https://img.shields.io/badge/platform-Linux-lightgrey)
[![Last Commit](https://img.shields.io/github/last-commit/1R1an1/MediaPlayer)](https://github.com/1R1an1/MediaPlayer/commits/master/)
[![Repo Stars](https://img.shields.io/github/stars/1R1an1/MediaPlayer?style=social)](https://github.com/1R1an1/MediaPlayer)

Reproductor de video nativo para Linux, construido con Avalonia UI y libmpv.
Controles en tema oscuro sobre el reproductor, integración con el escritorio vía MPRIS2 (GNOME, KDE, etc), y atajos de teclado estilo mpv.

---

## Capturas

### Reproductor

![Reproductor](screenshots/player.png)

### Playlist

![Playlist](screenshots/playlist.png)

---

## Formatos soportados

- Video: `.mp4` `.mkv` `.webm` `.avi` `.mov` `.flv` `.wmv` `.mpg` `.mpeg` `.m4v` `.ts`
- Audio: `.mp3` `.flac`

---

## Requisitos

| Requisito         | Detalle                              |
| ----------------- | ------------------------------------ |
| .NET SDK          | **10.0+**                            |
| Sistema operativo | Linux                                |
| libmpv            | `libmpv2`                            |
| ffmpeg / ffprobe  | Para extracción de covers y duración |

---

## Instalación

El repositorio incluye SharpUtils como submódulo.

Clonar con `--recursive`:

```bash
git clone --recursive https://github.com/1R1an1/MediaPlayer.git
cd MediaPlayer
```

O, si ya estaba clonado sin `--recursive`, inicializar el submódulo:

```bash
git clone https://github.com/1R1an1/MediaPlayer.git
cd MediaPlayer
git submodule update --init --recursive
```

Compilar e instalar:

```bash
dotnet publish -c Release -r linux-x64 --self-contained true
sudo ln -sf $(realpath ./bin/Release/net10.0/linux-x64/publish/MediaPlayer) /usr/local/bin/mp
```

---

## Atajos de teclado

| Acción                   | Tecla              |
| ------------------------ | ------------------ |
| Play / Pausa             | `Space`, `K`       |
| Siguiente                | `N`                |
| Anterior                 | `B`                |
| Retroceder 5s            | `←`                |
| Avanzar 5s               | `→`                |
| Retroceder 30s           | `Shift + ←`        |
| Avanzar 30s              | `Shift + →`        |
| Retroceder 60s           | `Ctrl + ←`         |
| Avanzar 60s              | `Ctrl + →`         |
| Volumen +5%              | `↑`, `+`, `0`      |
| Volumen -5%              | `↓`, `-`, `9`      |
| Mute                     | `M`                |
| Cambiar modo loop        | `L`                |
| Siguiente pista de audio | `J`                |
| Pantalla completa        | `F`                |
| Salir de fullscreen      | `Esc`              |
| Mostrar / ocultar cola   | `C`                |
| Abrir archivos           | `Ctrl + O`         |
| Abrir carpeta            | `Ctrl + Shift + O` |
| Salir                    | `Q`                |

---

## Issues

Si encontraste un bug o tenés una idea para mejorar la app, podés abrir un [Issue](https://github.com/1R1an1/MediaPlayer/issues) en el repositorio.

---

## Licencia

Este proyecto está bajo la licencia **Mozilla Public License 2.0 (MPL-2.0)**.

Ver [LICENSE](https://github.com/1R1an1/MediaPlayer/blob/master/LICENSE) para el texto completo.
