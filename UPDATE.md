# UPDATE — V-Eval-Ai_Engine

## [23/11/2026]
- **Tích Hợp Continuous Deployment (Auto-CD) Lên Máy Chủ Ubuntu Docker Server (`veval.io.vn`)**:
  - Bổ sung file workflow [`.github/workflows/cd-main.yml`](./.github/workflows/cd-main.yml) kích hoạt tự động mỗi khi có Pull Request được merge vào nhánh `main`.
  - Cấu hình 2 job tuần tự:
    1. **`ci-verify` (`runs-on: ubuntu-latest`)**: Chạy kiểm tra lint, build và automated tests trên hạ tầng GitHub Cloud.
    2. **`deploy-ubuntu` (`runs-on: [self-hosted, veval-prod]`)**: Gọi script `/data/Capstone/Scripts/deploy_service.sh ai-engine` trên máy chủ Ubuntu production.
  - Áp dụng cơ chế selective rebuild (`--no-deps`), đồng bộ Git hard reset nhánh `main` và bảo toàn kết nối Zero-Downtime của Cloudflare Tunnel.
