#!/usr/bin/env bash
set -euo pipefail

cp /etc/rabbitmq/definitions.json /etc/rabbitmq/definitions-enabled.json
exec docker-entrypoint.sh rabbitmq-server

