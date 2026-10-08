#!/bin/sh

if [ "${1:-}" = "-version" ]; then
    cat <<'EOF'
ffprobe version 7.0
libavutil      59.  8.100
libavcodec     61.  3.100
libavformat    61.  1.100
EOF
    exit 0
fi

printf '%s\n' '{"programs":[],"streams":[],"chapters":[],"format":{}}'
