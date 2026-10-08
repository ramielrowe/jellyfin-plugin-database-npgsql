#!/bin/sh

if [ "${1:-}" = "-version" ]; then
    cat <<'EOF'
ffmpeg version 7.0
libavutil      59.  8.100
libavcodec     61.  3.100
libavformat    61.  1.100
libavdevice    61.  1.100
libavfilter    10.  1.100
libswscale      8.  1.100
libswresample   5.  1.100
EOF
fi

exit 0
