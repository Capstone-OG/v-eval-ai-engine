# Script setup Marker PDF
Write-Host "Creating virtual environment in D:\CapstoneAI\OCR\marker_venv..." -ForegroundColor Cyan
python -m venv D:\CapstoneAI\OCR\marker_venv

Write-Host "Activating virtual environment..." -ForegroundColor Cyan
. D:\CapstoneAI\OCR\marker_venv\Scripts\Activate.ps1

Write-Host "Upgrading pip, setuptools, wheel..." -ForegroundColor Cyan
python -m pip install --upgrade pip setuptools wheel

Write-Host "Installing PyTorch with CUDA 12.1 support..." -ForegroundColor Cyan
pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu121

Write-Host "Installing marker-pdf..." -ForegroundColor Cyan
pip install marker-pdf

Write-Host "Checking CUDA availability in PyTorch..." -ForegroundColor Cyan
python -c "import torch; print('CUDA Available:', torch.cuda.is_available()); print('Device Name:', torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'None')"

Write-Host "Marker installation completed successfully!" -ForegroundColor Green
