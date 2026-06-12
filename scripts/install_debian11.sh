#!/usr/bin/env bash
set -euo pipefail

# Автоустановка Launcher Server на Debian 11
# Использование: sudo ./install_debian11.sh --repo <git-url> [--branch <branch>] [--port <port>]

REPO_URL=""
BRANCH="main"
PORT=5000
APP_USER="launcher"
APP_NAME="launcher-server"
APP_DIR="/opt/${APP_NAME}"
PUBLISH_DIR="${APP_DIR}/publish"
SERVICE_NAME="${APP_NAME}" 

print_usage() {
  echo "Usage: $0 --repo <git-url> [--branch <branch>] [--port <port>]"
}

# Parse args
while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo) shift; REPO_URL="$1" ;; 
    --branch) shift; BRANCH="$1" ;; 
    --port) shift; PORT="$1" ;; 
    -h|--help) print_usage; exit 0 ;;
    *) echo "Unknown arg: $1"; print_usage; exit 1 ;;
  esac
  shift
done

if [[ -z "$REPO_URL" ]]; then
  echo "Error: --repo is required"
  print_usage
  exit 1
fi

if [[ $EUID -ne 0 ]]; then
  echo "This script must be run as root (sudo)." >&2
  exit 1
fi

apt update
apt install -y wget apt-transport-https ca-certificates gnupg git iptables-utils

# Add Microsoft package repository for .NET
MS_KEYRING="/usr/share/keyrings/microsoft.gpg"
if [[ ! -f $MS_KEYRING ]]; then
  wget -qO- https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor > "$MS_KEYRING"
  echo "deb [arch=amd64 signed-by=$MS_KEYRING] https://packages.microsoft.com/debian/11/prod stable main" > /etc/apt/sources.list.d/microsoft-prod.list
fi

apt update

# Try to install aspnetcore runtime 10, fall back to 8/7 if 10 not available
RUNTIMES=("aspnetcore-runtime-10" "aspnetcore-runtime-8" "aspnetcore-runtime-7" "dotnet-runtime-7" "dotnet-runtime-6")
INSTALLED_RUNTIME=""
for pkg in "${RUNTIMES[@]}"; do
  if apt-cache show "$pkg" >/dev/null 2>&1; then
    echo "Installing $pkg"
    apt install -y "$pkg"
    INSTALLED_RUNTIME="$pkg"
    break
  fi
done

if [[ -z "$INSTALLED_RUNTIME" ]]; then
  echo "No supported .NET runtime package found in apt cache. Attempting to install dotnet via script." 
  wget https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel LTS --install-dir /usr/share/dotnet
  ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
fi

# Create a system user
if ! id -u "$APP_USER" >/dev/null 2>&1; then
  useradd --system --no-create-home --shell /usr/sbin/nologin "$APP_USER"
fi

# Prepare directories
mkdir -p "$PUBLISH_DIR"
chown -R "$APP_USER":"$APP_USER" "$APP_DIR"

# Clone repo and publish
TMPDIR=$(mktemp -d)
cleanup() { rm -rf "$TMPDIR"; }
trap cleanup EXIT

echo "Cloning $REPO_URL (branch $BRANCH)"
if ! git clone --depth 1 --branch "$BRANCH" "$REPO_URL" "$TMPDIR/repo"; then
  echo "Git clone failed" >&2
  exit 1
fi

cd "$TMPDIR/repo"
if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet not found. Aborting." >&2
  exit 1
fi

echo "Publishing project"
# Try to detect solution or project and publish
PROJECT_FILE=$(find . -maxdepth 3 -name "*.csproj" | head -n 1 || true)
if [[ -z "$PROJECT_FILE" ]]; then
  echo "No .csproj found in repository root or subfolders (depth 3). Aborting." >&2
  exit 1
fi

dotnet publish "$PROJECT_FILE" -c Release -o "$PUBLISH_DIR"

# Ensure ownership
chown -R "$APP_USER":"$APP_USER" "$APP_DIR"
chmod -R 750 "$APP_DIR"

# Create systemd service
SERVICE_PATH="/etc/systemd/system/${SERVICE_NAME}.service"
cat > "$SERVICE_PATH" <<EOF
[Unit]
Description=Launcher Server
After=network.target

[Service]
WorkingDirectory=${PUBLISH_DIR}
ExecStart=/usr/bin/dotnet ${PUBLISH_DIR}/$(basename "${PROJECT_FILE%.*}").dll --urls http://0.0.0.0:${PORT}
User=${APP_USER}
Restart=always
RestartSec=10
SyslogIdentifier=${SERVICE_NAME}
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now "${SERVICE_NAME}.service"

# Open firewall port via ufw if available
if command -v ufw >/dev/null 2>&1; then
  ufw allow "$PORT"/tcp || true
fi

echo "Installation complete. Service: ${SERVICE_NAME}. Port: ${PORT}."
echo "journalctl -u ${SERVICE_NAME} -f" for logs.

exit 0
