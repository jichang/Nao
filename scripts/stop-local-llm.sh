#!/bin/bash
# Stop the local Ollama server if it was started by start-local-llm.sh
set -e

if pgrep -x "ollama" > /dev/null; then
    echo "Stopping Ollama server..."
    pkill -x "ollama" || true
    echo "Ollama server stopped."
else
    echo "Ollama server is not running."
fi
