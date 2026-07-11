#!/bin/bash
# Start a local LLM using Ollama for testing.
# This script ensures Ollama is installed, running, and has a model pulled.
#
# Usage:
#   ./scripts/start-local-llm.sh [model]
#
# Default model: qwen2.5:3b (good balance of capability and speed for testing)
# Alternatives: llama3.2:1b, phi3:mini, qwen2.5:0.5b (smallest), qwen3:latest

set -e

MODEL="${1:-qwen2.5:3b}"
OLLAMA_HOST="${OLLAMA_HOST:-http://localhost:11434}"

echo "=== Nao Local LLM Setup ==="
echo "Model: $MODEL"
echo "Host:  $OLLAMA_HOST"
echo ""

# Check if Ollama is installed
if ! command -v ollama &> /dev/null; then
    echo "Ollama not found. Installing..."
    curl -fsSL https://ollama.com/install.sh | sh
    echo "Ollama installed."
fi

# Check if Ollama server is running
if ! curl -s "$OLLAMA_HOST/api/tags" > /dev/null 2>&1; then
    echo "Starting Ollama server..."
    ollama serve &
    OLLAMA_PID=$!
    echo "Ollama server started (PID: $OLLAMA_PID)"

    # Wait for server to be ready
    echo -n "Waiting for server..."
    for i in $(seq 1 30); do
        if curl -s "$OLLAMA_HOST/api/tags" > /dev/null 2>&1; then
            echo " ready."
            break
        fi
        echo -n "."
        sleep 1
    done

    if ! curl -s "$OLLAMA_HOST/api/tags" > /dev/null 2>&1; then
        echo ""
        echo "ERROR: Ollama server failed to start within 30 seconds."
        exit 1
    fi
else
    echo "Ollama server is already running."
fi

# Check if the model is already pulled
if ollama list 2>/dev/null | grep -q "$MODEL"; then
    echo "Model '$MODEL' is already available."
else
    echo "Pulling model '$MODEL'..."
    ollama pull "$MODEL"
    echo "Model '$MODEL' pulled successfully."
fi

# Verify the model works
echo ""
echo "Verifying model..."
RESPONSE=$(curl -s "$OLLAMA_HOST/api/generate" \
    -d "{\"model\":\"$MODEL\",\"prompt\":\"Say hello in one word.\",\"stream\":false}" \
    | grep -o '"response":"[^"]*"' | head -1)

if [ -n "$RESPONSE" ]; then
    echo "Model is working: $RESPONSE"
else
    echo "WARNING: Model verification returned empty response. It may still work."
fi

echo ""
echo "=== Local LLM Ready ==="
echo "Endpoint: $OLLAMA_HOST"
echo "Model:    $MODEL"
echo ""
echo "Set these environment variables for tests:"
echo "  export NAO_LLM_ENDPOINT=$OLLAMA_HOST"
echo "  export NAO_LLM_MODEL=$MODEL"
