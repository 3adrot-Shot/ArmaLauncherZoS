#!/bin/bash
# Скрипт установки AI Model Launcher Server
# Для Ubuntu/Debian сервера 176.124.222.151

set -e

echo "????????????????????????????????????????????????????????????????"
echo "?     AI Model Launcher Server - Installation Script          ?"
echo "????????????????????????????????????????????????????????????????"
echo ""

# Переменные
SERVER_IP="176.124.222.151"
APP_PORT=5000
INSTALL_DIR="/opt/launcher-server"
DATA_DIR="/var/lib/launcher"
SERVICE_USER="launcher"

# 1. Установка .NET 10 Runtime
echo "[1/7] Установка .NET 10 Runtime..."
if ! command -v dotnet &> /dev/null; then
    wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
    chmod +x dotnet-install.sh
    ./dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet
    ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
    rm dotnet-install.sh
fi
dotnet --info

# 2. Установка MinIO (локальное S3-совместимое хранилище)
echo "[2/7] Установка MinIO..."
if ! command -v minio &> /dev/null; then
    wget https://dl.min.io/server/minio/release/linux-amd64/minio -O /usr/local/bin/minio
    chmod +x /usr/local/bin/minio
fi

# 3. Создание пользователя и директорий
echo "[3/7] Создание пользователя и директорий..."
if ! id "$SERVICE_USER" &>/dev/null; then
    useradd -r -s /bin/false $SERVICE_USER
fi

mkdir -p $INSTALL_DIR
mkdir -p $DATA_DIR/{keys,minio-data}
mkdir -p /var/log/launcher

chown -R $SERVICE_USER:$SERVICE_USER $DATA_DIR
chown -R $SERVICE_USER:$SERVICE_USER /var/log/launcher

# 4. Создание systemd сервиса для MinIO
echo "[4/7] Настройка MinIO сервиса..."
cat > /etc/systemd/system/minio.service << 'EOF'
[Unit]
Description=MinIO Object Storage
After=network.target

[Service]
User=launcher
Group=launcher
Environment="MINIO_ROOT_USER=minioadmin"
Environment="MINIO_ROOT_PASSWORD=minioadmin123secure"
ExecStart=/usr/local/bin/minio server /var/lib/launcher/minio-data --console-address ":9001"
Restart=always
RestartSec=10
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF

# 5. Создание systemd сервиса для Launcher Server
echo "[5/7] Настройка Launcher Server сервиса..."
cat > /etc/systemd/system/launcher-server.service << EOF
[Unit]
Description=AI Model Launcher Server
After=network.target minio.service

[Service]
User=$SERVICE_USER
Group=$SERVICE_USER
WorkingDirectory=$INSTALL_DIR
ExecStart=/usr/bin/dotnet $INSTALL_DIR/LauncherServer.dll
Restart=always
RestartSec=10
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:$APP_PORT
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF

# 6. Настройка файрвола (если ufw установлен)
echo "[6/7] Настройка файрвола..."
if command -v ufw &> /dev/null; then
    ufw allow $APP_PORT/tcp comment "Launcher Server API"
    ufw allow 9000/tcp comment "MinIO API"
    ufw allow 9001/tcp comment "MinIO Console"
fi

# 7. Запуск сервисов
echo "[7/7] Запуск сервисов..."
systemctl daemon-reload
systemctl enable minio
systemctl start minio

echo ""
echo "????????????????????????????????????????????????????????????????"
echo "?                    Установка завершена!                      ?"
echo "????????????????????????????????????????????????????????????????"
echo ""
echo "Следующие шаги:"
echo ""
echo "1. Скопируйте опубликованное приложение в $INSTALL_DIR:"
echo "   scp -r ./publish/* root@$SERVER_IP:$INSTALL_DIR/"
echo ""
echo "2. Создайте бакет в MinIO:"
echo "   mc alias set local http://localhost:9000 minioadmin minioadmin123secure"
echo "   mc mb local/ai-models"
echo ""
echo "3. Запустите сервер:"
echo "   systemctl start launcher-server"
echo "   systemctl status launcher-server"
echo ""
echo "4. Проверьте работу:"
echo "   curl http://$SERVER_IP:$APP_PORT/health"
echo ""
echo "MinIO Console: http://$SERVER_IP:9001"
echo "API Endpoint:  http://$SERVER_IP:$APP_PORT"
echo ""
