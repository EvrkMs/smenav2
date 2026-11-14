# Образ сервера «Смена»: сервер + три собранных веб-интерфейса.
# Сборка — из корня репозитория: docker build -t smenav2 .
# Образы берутся с mirror.gcr.io и mcr.microsoft.com: Docker Hub может быть недоступен.

# ---- 1. Веб-интерфейсы (касса, панель, кабинет сотрудника) ----
FROM mirror.gcr.io/library/node:24-alpine AS web
WORKDIR /src/web

# Сначала только описания зависимостей: слой с node_modules пересобирается,
# лишь когда меняются они, а не при каждой правке кода.
COPY web/cashier/package.json web/cashier/package-lock.json cashier/
COPY web/panel/package.json web/panel/package-lock.json panel/
COPY web/miniapp/package.json web/miniapp/package-lock.json miniapp/
RUN for app in cashier panel miniapp; do (cd $app && npm ci --no-audit --no-fund) || exit 1; done

COPY web/ ./
# Vite кладёт сборки в ../../src/Smena.Server/wwwroot/<приложение>.
RUN for app in cashier panel miniapp; do (cd $app && npm run build) || exit 1; done

# ---- 2. Сервер ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Smena.Core/Smena.Core.csproj src/Smena.Core/
COPY src/Smena.Server/Smena.Server.csproj src/Smena.Server/
# Сборка под одну платформу: иначе в образ попадают библиотеки обработки
# изображений для Windows, macOS и ARM — сотни лишних мегабайт.
RUN dotnet restore src/Smena.Server/Smena.Server.csproj -r linux-x64

COPY src/Smena.Core/ src/Smena.Core/
COPY src/Smena.Server/ src/Smena.Server/
COPY --from=web /src/src/Smena.Server/wwwroot/ src/Smena.Server/wwwroot/
RUN dotnet publish src/Smena.Server/Smena.Server.csproj -c Release -r linux-x64 --self-contained false -o /app --no-restore

# ---- 3. Образ для запуска ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# nftables — для сторожа исходящих подключений (docker/entrypoint.sh).
RUN apt-get update \
    && apt-get install -y --no-install-recommends nftables \
    && rm -rf /var/lib/apt/lists/*

# Каталоги под тома создаются заранее и отдаются непривилегированному пользователю:
# новый том наследует владельца каталога, на который смонтирован.
#   /data/photos — файлы фото;
#   /data/keys   — ключи, которыми подписана cookie панели (иначе каждый
#                  перезапуск контейнера разлогинивал бы root).
RUN mkdir -p /data/photos /data/keys && chown -R $APP_UID /data

COPY --from=build /app ./
COPY --chmod=755 docker/entrypoint.sh /usr/local/bin/smena-entrypoint

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Photos__StoragePath=/data/photos \
    DataProtection__KeysPath=/data/keys

# Запускается от root: скрипт ставит правила сторожа и сам переходит на
# пользователя app без прав. Контейнеру нужна возможность NET_ADMIN.
EXPOSE 8080
VOLUME ["/data/photos", "/data/keys"]

ENTRYPOINT ["/usr/local/bin/smena-entrypoint"]
