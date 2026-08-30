# Script setup Ollama & Qwen 2.5-VL 3B
Write-Host "Creating models directory in D:\CapstoneAI\OCR\ollama_models..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "D:\CapstoneAI\OCR\ollama_models" | Out-Null

$env:OLLAMA_MODELS = "D:\CapstoneAI\OCR\ollama_models"
Write-Host "Set OLLAMA_MODELS to $env:OLLAMA_MODELS" -ForegroundColor Cyan

Write-Host "Starting Ollama server in background..." -ForegroundColor Cyan
$ollamaProc = Start-Process -FilePath "ollama" -ArgumentList "serve" -NoNewWindow -PassThru

Write-Host "Waiting 5 seconds for Ollama server to start..." -ForegroundColor Cyan
Start-Sleep -Seconds 5

Write-Host "Pulling qwen2.5vl:3b (this may take a few minutes)..." -ForegroundColor Cyan
ollama pull qwen2.5vl:3b

Write-Host "Qwen 2.5vl 3B pulled successfully!" -ForegroundColor Green

# Optional: Stop Ollama server after pulling so it doesn't consume resources
Write-Host "Stopping background Ollama server..." -ForegroundColor Cyan
Stop-Process -Id $ollamaProc.Id -Force
Write-Host "Setup Qwen completed successfully!" -ForegroundColor Green
