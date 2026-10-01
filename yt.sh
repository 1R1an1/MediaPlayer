#!/bin/bash

set -e

usage() {
    echo "Uso: $0 video <URL>" >&2
    echo "     $0 musica <mp3|flac> <URL>" >&2
    echo "     $0 musicaR <mp3|flac> <URL>" >&2
    echo "     $0 recortar" >&2
    [ -n "$1" ] && echo "Error: $1" >&2
    exit 1
}

case "$1" in
  video)
    [ $# -ne 2 ] && usage "'video' requiere una URL."
    yt-dlp -f "bv*+ba/b" --merge-output-format mkv --embed-metadata --embed-thumbnail --no-playlist "$2"
    ;;
  musica)
    [ $# -ne 3 ] && usage "'musica' requiere formato y URL."
    [ "$2" != "mp3" ] && [ "$2" != "flac" ] && usage "Formato inválido: '$2'. Debe ser mp3 o flac."
    yt-dlp -x -f "ba" --audio-format "$2" --audio-quality 0 --embed-metadata --embed-thumbnail --no-playlist "$3"
    ;;
  musicaR)
    [ $# -ne 3 ] && usage "'musicaR' requiere formato y URL."
    [ "$2" != "mp3" ] && [ "$2" != "flac" ] && usage "Formato inválido: '$2'. Debe ser mp3 o flac."
    yt-dlp -x -f "ba" --audio-format "$2" --audio-quality 0 --embed-metadata --embed-thumbnail --ppa "ThumbnailsConvertor+ffmpeg_o:-vf crop=ih:ih" --no-playlist "$3"
    ;;
  recortar)
    [ $# -ne 1 ] && usage "'recortar' no toma argumentos."
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
