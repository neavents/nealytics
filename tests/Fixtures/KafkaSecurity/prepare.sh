#!/bin/sh
set -eu
umask 077
: "${FIXTURE_SASL_PASSWORD:?Fixture password is required}"
case "$FIXTURE_SASL_PASSWORD" in
  *[!a-zA-Z0-9-]*|'') exit 2 ;;
esac
root=/tmp/kafka-security
mkdir -p "$root"
openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 2 -subj '/CN=Ephemeral Kafka fixture CA' \
  -addext 'basicConstraints=critical,CA:TRUE' -addext 'keyUsage=critical,keyCertSign,cRLSign' \
  -keyout "$root/ca.key" -out "$root/ca.pem" >"$root/certificate.log" 2>&1
openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 2 -subj '/CN=Untrusted Kafka fixture CA' \
  -addext 'basicConstraints=critical,CA:TRUE' -addext 'keyUsage=critical,keyCertSign,cRLSign' \
  -keyout "$root/wrong-ca.key" -out "$root/wrong-ca.pem" >>"$root/certificate.log" 2>&1
for name in server mismatch; do
  openssl req -new -newkey rsa:2048 -nodes -sha256 -subj "/CN=$name" \
    -keyout "$root/$name.key" -out "$root/$name.csr" >>"$root/certificate.log" 2>&1
  if [ "$name" = server ]; then
    subject='DNS:broker,DNS:localhost,IP:127.0.0.1'
  else
    subject='DNS:unexpected.invalid'
  fi
  cat >"$root/$name.ext" <<EXT
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=$subject
EXT
  openssl x509 -req -in "$root/$name.csr" -CA "$root/ca.pem" -CAkey "$root/ca.key" \
    -CAcreateserial -days 1 -sha256 -extfile "$root/$name.ext" -out "$root/$name.crt" >>"$root/certificate.log" 2>&1
  openssl pkcs8 -topk8 -nocrypt -in "$root/$name.key" -out "$root/$name.pkcs8" >>"$root/certificate.log" 2>&1
  cat "$root/$name.pkcs8" "$root/$name.crt" "$root/ca.pem" >"$root/$name.pem"
done
chmod 644 "$root/ca.pem" "$root/wrong-ca.pem"
cat >"$root/server.properties" <<CONFIG
process.roles=broker,controller
node.id=1
controller.quorum.voters=1@localhost:9093
controller.listener.names=CONTROLLER
inter.broker.listener.name=ADMIN
listeners=ADMIN://0.0.0.0:9092,CONTROLLER://0.0.0.0:9093,TLS://0.0.0.0:9094,AUTH://0.0.0.0:9095,MISMATCH://0.0.0.0:9096
advertised.listeners=ADMIN://broker:9092,TLS://broker:9094,AUTH://broker:9095,MISMATCH://broker:9096
listener.security.protocol.map=ADMIN:PLAINTEXT,CONTROLLER:PLAINTEXT,TLS:SSL,AUTH:SASL_SSL,MISMATCH:SSL
ssl.keystore.type=PEM
ssl.keystore.location=$root/server.pem
ssl.client.auth=none
listener.name.mismatch.ssl.keystore.location=$root/mismatch.pem
sasl.enabled.mechanisms=PLAIN,SCRAM-SHA-256,SCRAM-SHA-512
listener.name.auth.plain.sasl.jaas.config=org.apache.kafka.common.security.plain.PlainLoginModule required user_fixture="$FIXTURE_SASL_PASSWORD";
listener.name.auth.scram-sha-256.sasl.jaas.config=org.apache.kafka.common.security.scram.ScramLoginModule required;
listener.name.auth.scram-sha-512.sasl.jaas.config=org.apache.kafka.common.security.scram.ScramLoginModule required;
log.dirs=$root/logs
offsets.topic.replication.factor=1
transaction.state.log.replication.factor=1
transaction.state.log.min.isr=1
group.initial.rebalance.delay.ms=0
auto.create.topics.enable=false
CONFIG
cluster=$(/opt/kafka/bin/kafka-storage.sh random-uuid)
/opt/kafka/bin/kafka-storage.sh format --config "$root/server.properties" --cluster-id="$cluster" \
  --add-scram="SCRAM-SHA-256=[name=fixture,password=$FIXTURE_SASL_PASSWORD]" \
  --add-scram="SCRAM-SHA-512=[name=fixture,password=$FIXTURE_SASL_PASSWORD]"
exec /opt/kafka/bin/kafka-server-start.sh "$root/server.properties"
