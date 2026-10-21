#!/bin/bash

# Dependencies: yt-dlp, ffmpeg and python-mutagen (yt-dlp uses mutagen to embed tags in mp3/flac).
miss=0
command -v yt-dlp >/dev/null 2>&1 || ((miss++))
command -v ffmpeg >/dev/null 2>&1 || ((miss++))
python3 -c "import mutagen" 2>/dev/null || ((miss++))

set -e

if [[ $miss -gt 0 ]]; then
    if   command -v apt >/dev/null 2>&1; then cmd="sudo apt install"; m="python3-mutagen"
    elif command -v pacman >/dev/null 2>&1; then cmd="sudo pacman -S --needed"; m="python-mutagen"
    elif command -v dnf >/dev/null 2>&1; then cmd="sudo dnf install"; m="python3-mutagen"
    else echo "$miss package(s) are missing and no package manager was detected." >&2; exit 1; fi
    read -rp "$miss package(s) are missing. Install? [y/N] " yn
    [[ "$yn" =~ ^[Yy]$ ]] || exit 1
    $cmd $m yt-dlp ffmpeg || exit 1
fi

usage() {
    echo "Usage: $0 video <URL>" >&2
    echo "       $0 music <mp3|flac> <URL>" >&2
    echo "       $0 musicC <mp3|flac> <URL>" >&2
    echo "       $0 crop" >&2
    [ -n "$1" ] && echo "Error: $1" >&2
    exit 1
}

case "$1" in
  video)
    [ $# -ne 2 ] && usage "'video' requires a URL."
    yt-dlp -f "bv*+ba/b" --merge-output-format mkv --embed-metadata --embed-thumbnail --no-playlist "$2"
    ;;
  music)
    [ $# -ne 3 ] && usage "'music' requires a format and a URL."
    [ "$2" != "mp3" ] && [ "$2" != "flac" ] && usage "Invalid format: '$2'. Must be mp3 or flac."
    yt-dlp -x -f "ba" --audio-format "$2" --audio-quality 0 --embed-metadata --embed-thumbnail --no-playlist "$3"
    ;;
  musicC)
    [ $# -ne 3 ] && usage "'musicC' requires a format and a URL."
    [ "$2" != "mp3" ] && [ "$2" != "flac" ] && usage "Invalid format: '$2'. Must be mp3 or flac."
    yt-dlp -x -f "ba" --audio-format "$2" --audio-quality 0 --embed-metadata --embed-thumbnail --ppa "ThumbnailsConvertor+ffmpeg_o:-vf crop=ih:ih" --no-playlist "$3"
    ;;
  crop)
    [ $# -ne 1 ] && usage "'crop' doesn't take arguments."
    for f in *.{flac,mp3}; do
      [ -f "$f" ] || continue
      case "$f" in
        *.flac) tmp="${f%.flac}.tmp.flac"; ffmpeg -y -i "$f" -map 0:a -map 0:v -c:a copy -c:v mjpeg -vf "crop=ih:ih" "$tmp" && mv "$tmp" "$f" ;;
        *.mp3) tmp="${f%.mp3}.tmp.mp3"; ffmpeg -y -i "$f" -map 0:a -map 0:v -c:a copy -c:v mjpeg -vf "crop=ih:ih" -id3v2_version 3 "$tmp" && mv "$tmp" "$f" ;;
      esac
    done
    ;;
  *) usage ;;
esac
