#!/bin/bash
# Запуск сервера в контейнере со сторожем исходящих подключений.
#
# Сторож — правила nftables в сетевом пространстве этого контейнера. Наружу
# разрешено только то, что серверу действительно нужно: база, SOCKS-прокси для
# Telegram и API «МойСклад». Всё остальное ядро отбрасывает. Даже если в сервер
# попадёт чужой код (например, через уязвимость в разборе картинок), отправить
# данные на посторонний адрес он не сможет.
#
# Правила ставит этот скрипт от root, затем сервер запускается от пользователя
# app без каких-либо прав: изменить правила или остановить сторожа он не может.
# Если правила поставить не удалось, контейнер не запускается.
#
# Адреса разрешённых узлов (они могут меняться) перечитываются раз в
# EGRESS_REFRESH_SECONDS секунд. Если имя временно не разрешается, остаются
# прежние адреса.
#
# Переменные:
#   EGRESS_FIREWALL=off       — выключить сторожа (только для отладки).
#   EGRESS_ALLOW=host:port,…  — дополнительные разрешённые узлы.
set -euo pipefail

APP_USER=app
REFRESH_SECONDS=${EGRESS_REFRESH_SECONDS:-300}

log() { echo "egress: $*" >&2; }

# ---- Что разрешено ---------------------------------------------------------

# Узлы вида host:port, куда серверу нужно ходить, — из его же настроек.
allowed_endpoints() {
    local part key value host='' port=5432

    # База: Host и Port из строки подключения Npgsql.
    IFS=';' read -ra parts <<< "${Database__ConnectionString:-}"
    for part in "${parts[@]}"; do
        key=$(echo "${part%%=*}" | tr -d ' ' | tr '[:upper:]' '[:lower:]')
        value=$(echo "${part#*=}" | tr -d ' ')
        case "$key" in
            host|server) host=$value ;;
            port) port=$value ;;
        esac
    done
    [ -n "$host" ] && echo "$host:$port"

    # Telegram — только через SOCKS-прокси: сам api.telegram.org не разрешается.
    if [ -n "${Telegram__ProxyUri:-}" ]; then
        local proxy=${Telegram__ProxyUri#*://}
        proxy=${proxy%%/*}
        proxy=${proxy##*@}
        echo "$proxy"
    fi

    # «МойСклад» — напрямую, если настроен доступ.
    if [ -n "${MoySklad__Token:-}${MoySklad__Login:-}" ]; then
        local base=${MoySklad__BaseUrl:-https://api.moysklad.ru/api/remap/1.2}
        base=${base#*://}
        base=${base%%/*}
        case "$base" in *:*) echo "$base" ;; *) echo "$base:443" ;; esac
    fi

    if [ -n "${EGRESS_ALLOW:-}" ]; then
        tr ',' '\n' <<< "$EGRESS_ALLOW" | tr -d ' ' | grep -v '^$'
    fi
}

# ---- Правила ---------------------------------------------------------------

# Таблица целиком: политика «запретить» и один набор пар «адрес . порт».
# Ответы на входящие запросы (от Caddy) проходят как уже установленные соединения.
# DNS Docker (127.0.0.11) работает через lo.
apply_table() {
    nft -f - <<'NFT'
table inet smena_egress
delete table inet smena_egress
table inet smena_egress {
    set allowed {
        type ipv4_addr . inet_service
    }
    chain output {
        type filter hook output priority filter; policy drop;
        oifname "lo" accept
        ct state established,related accept
        ip daddr . tcp dport @allowed accept
        counter comment "dropped by egress guard"
    }
}
NFT
}

declare -A KNOWN=()   # host:port → последние удачно разрешённые адреса

# Разрешает имена и атомарно заменяет набор. Имя, которое сейчас не
# разрешается, сохраняет прежние адреса.
refresh() {
    local endpoint host port addresses elements=''
    while read -r endpoint; do
        [ -z "$endpoint" ] && continue
        host=${endpoint%:*}
        port=${endpoint##*:}
        if [[ ! $port =~ ^[0-9]+$ ]]; then
            log "пропущено: $endpoint — нет порта"
            continue
        fi
        if [[ $host =~ ^[0-9.]+$ ]]; then
            addresses=$host
        else
            addresses=$(getent ahostsv4 "$host" 2>/dev/null | awk '{print $1}' | sort -u | tr '\n' ' ' || true)
        fi
        if [ -n "${addresses// /}" ]; then
            KNOWN[$endpoint]=$addresses
        elif [ -z "${KNOWN[$endpoint]:-}" ]; then
            log "$host пока не разрешается"
        fi
        for address in ${KNOWN[$endpoint]:-}; do
            elements+="${elements:+, }$address . $port"
        done
    done < <(allowed_endpoints | sort -u)

    {
        echo "flush set inet smena_egress allowed"
        [ -n "$elements" ] && echo "add element inet smena_egress allowed { $elements }"
    } | nft -f -
}

# ---- Запуск ----------------------------------------------------------------

REFRESHER=
if [ "${EGRESS_FIREWALL:-on}" = off ]; then
    log "ВЫКЛЮЧЕН (EGRESS_FIREWALL=off): исходящие подключения не ограничены"
else
    if ! apply_table 2>/dev/null; then
        log "не удалось поставить правила. Контейнеру нужна возможность NET_ADMIN (cap_add)."
        exit 70
    fi
    refresh
    log "разрешено: $(allowed_endpoints | sort -u | tr '\n' ' ')"
    ( while sleep "$REFRESH_SECONDS"; do refresh || log "обновление адресов не удалось"; done ) &
    REFRESHER=$!
fi

# Сервер — от пользователя app, без возможностей ядра и без права их получить
# (no_new_privs): правила сторожа ему недоступны.
setpriv --reuid="$APP_USER" --regid="$APP_USER" --init-groups \
    --inh-caps=-all --ambient-caps=-all --bounding-set=-all --no-new-privs \
    -- env HOME="/home/$APP_USER" dotnet /app/Smena.Server.dll &
APP=$!

trap 'kill -TERM "$APP" 2>/dev/null' TERM INT

set +e
wait "$APP"
status=$?
# wait прерывается сигналом раньше, чем сервер завершился: ждём до конца.
while kill -0 "$APP" 2>/dev/null; do
    wait "$APP"
    status=$?
done
[ -n "$REFRESHER" ] && kill "$REFRESHER" 2>/dev/null
exit "$status"
