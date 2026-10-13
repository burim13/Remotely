#!/bin/bash
# Interoperability test on a Mac: the Swift code against the real .NET server and the .NET implementation of the protocol, in both directions.
#   A. Swift host (fake screen)  <->  .NET viewer (Remotely.Probe)
#   B. .NET fake host (Remotely.Probe host-fake)  <->  Swift viewer (remotely-cli)
# Needs the .NET 10 SDK. Usage: mac/ci/interop.sh <path to remotely-host-mac> <path to remotely-cli>
set -uo pipefail
HOST_BIN="$1"; CLI_BIN="$2"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
W="${RUNNER_TEMP:-/tmp}/remotely-interop"; rm -rf "$W"; mkdir -p "$W"
FAIL=0
# Every external command gets a hard time limit, so a stuck handshake can never hang the whole run (macOS has no `timeout`; perl's alarm does the same job).
T() { local secs="$1"; shift; perl -e 'alarm shift; exec @ARGV' "$secs" "$@"; }
check() { if [ "$2" -eq 0 ]; then echo "CHECK PASS: $1"; else echo "CHECK FAIL: $1"; FAIL=$((FAIL+1)); fi; }
contains() { grep -q -- "$2" "$1"; echo $?; }   # prints 0 when file $1 contains $2

echo "== build the .NET side"; date
dotnet publish "$ROOT/src/Remotely.Server" -c Release -o "$W/server" -v q 2>&1 | grep -E "error" ; dotnet publish "$ROOT/src/Remotely.Probe" -c Release -o "$W/probe" -v q 2>&1 | grep -E "error"

export Remotely__TlsEnabled=false Remotely__DataDir="$W/srvdata" Remotely__PublicPort=19443 Remotely__AdminPort=19181
dotnet "$W/server/Remotely.Server.dll" > "$W/server.log" 2>&1 &
SERVER_PID=$!
sleep 6
URL=ws://127.0.0.1:19443/ws
code() { curl -s -m 10 -X POST http://127.0.0.1:19181/admin/pair-codes | python3 -c "import sys,json;print(json.load(sys.stdin)['code'])"; }
probe() { local home="$1"; shift; HOME="$home" T 60 dotnet "$W/probe/Remotely.Probe.dll" "$@"; }
cli() { T 60 "$CLI_BIN" "$@"; }

echo; echo "===== A. Swift host <-> .NET viewer ====="
V1="$W/viewer1"; V2="$W/viewer2"; V3="$W/viewer3"; mkdir -p "$V1" "$V2" "$V3"
ID1=$(probe "$V1" pair --server $URL --code "$(code)" --name dotnet-viewer --insecure | tail -1)
ID2=$(probe "$V2" pair --server $URL --code "$(code)" --name view-only-viewer --insecure | tail -1)
probe "$V3" pair --server $URL --code "$(code)" --name stranger --insecure > /dev/null
check "three .NET viewers paired" $([ ${#ID1} -eq 64 ] && [ ${#ID2} -eq 64 ]; echo $?)

export REMOTELY_HOME="$W/hosthome"
mkdir -p "$REMOTELY_HOME/Documents/Remotely/Share"
printf 'shared by the swift host' > "$REMOTELY_HOME/Documents/Remotely/Share/hello.txt"
T 30 "$HOST_BIN" setup --server $URL --insecure --code "$(code)" --name swift-host --trust "$ID1" --view-only "$ID2" > "$W/hostsetup.log" 2>&1
cat "$W/hostsetup.log"
"$HOST_BIN" run --fake-screen --seconds 75 > "$W/swifthost.log" 2>&1 &
HOST_PID=$!
for i in $(seq 1 40); do probe "$V1" list 2>/dev/null | grep -q "online.*swift-host" && break; sleep 1; done
HID=$(probe "$V1" list | awk '/swift-host/ {print $4}')
check "swift host online and listed" $([ ${#HID} -eq 64 ]; echo $?)

probe "$V1" watch "$HID" --out "$W/frames-a" --count 2 --seconds 12 > "$W/a-watch.log" 2>&1; cat "$W/a-watch.log"
check "A: frames arrive from the swift host" $(contains "$W/a-watch.log" "desktop=Default")
check "A: the hello announces control, clipboard and files" $(contains "$W/a-watch.log" "Control, Clipboard, Files")

# The host announces its own clipboard to a viewer that just connected; check that on its own, before the viewer overwrites the host clipboard in the next call.
probe "$V1" control "$HID" --wait 2 > "$W/a-hostclip.log" 2>&1; cat "$W/a-hostclip.log"
echo "payload from dotnet" > "$W/up-a.txt"
probe "$V1" control "$HID" --move 16384,16384 --key 65 --clip "clip from dotnet" --upload "$W/up-a.txt" --list-share --wait 2 > "$W/a-control.log" 2>&1; cat "$W/a-control.log"
( cd "$W" && probe "$V1" control "$HID" --download hello.txt --wait 1 > "$W/a-download.log" 2>&1 ); cat "$W/a-download.log"
( cd "$W" && probe "$V1" control "$HID" --download "../../etc/hosts" --wait 1 > "$W/a-traversal.log" 2>&1 ); cat "$W/a-traversal.log"
sleep 1
check "A: mouse move reached the swift host" $(contains "$W/swifthost.log" "INPUT mouseMove x=16384 y=16384")
check "A: key press reached the swift host (VK 65)" $(contains "$W/swifthost.log" "INPUT keyDown x=0 y=0 a1=65")
check "A: clipboard reached the swift host" $(contains "$W/swifthost.log" "CLIPBOARD set: clip from dotnet")
check "A: clipboard of the swift host reached the viewer" $(contains "$W/a-hostclip.log" "clipboard from host: fake host clipboard")
check "A: upload stored by the swift host, identical" $([ "$(cat "$REMOTELY_HOME/Documents/Remotely/Received/up-a.txt" 2>/dev/null)" = "payload from dotnet" ]; echo $?)
check "A: shared folder listed" $(contains "$W/a-control.log" "hello.txt")
check "A: shared file downloaded, identical" $([ "$(cat "$W/downloads/hello.txt" 2>/dev/null)" = "shared by the swift host" ]; echo $?)
check "A: path traversal refused" $(contains "$W/a-traversal.log" "download failed")

probe "$V2" control "$HID" --move 1,1 --key 66 --clip "view only must not work" --wait 2 > "$W/a-viewonly.log" 2>&1; cat "$W/a-viewonly.log"
sleep 1
check "A: a view-only viewer connects with no capabilities" $(contains "$W/a-viewonly.log" "Capabilities: None")
check "A: a view-only viewer cannot inject input" $(grep -q "x=1 y=1" "$W/swifthost.log"; [ $? -ne 0 ]; echo $?)
check "A: a view-only viewer cannot write the clipboard" $(grep -q "view only must not work" "$W/swifthost.log"; [ $? -ne 0 ]; echo $?)

probe "$V3" watch "$HID" --out "$W/frames-stranger" --count 1 --seconds 6 > "$W/a-stranger.log" 2>&1; cat "$W/a-stranger.log"
check "A: an untrusted viewer gets no frames" $(grep -q "desktop=" "$W/a-stranger.log"; [ $? -ne 0 ]; echo $?)
check "A: the swift host logs the refusal with the id to trust" $(contains "$W/swifthost.log" "Refused viewer")
kill $HOST_PID 2>/dev/null; wait $HOST_PID 2>/dev/null

echo; echo "===== B. .NET host <-> Swift viewer ====="
rm -rf "$W/downloads"   # the file sink never overwrites; start B with a clean folder
export HOME="$W/clihome"; mkdir -p "$HOME"
CLIID=$(cli pair --server $URL --code "$(code)" --name swift-viewer --insecure | tail -1)
check "swift viewer paired" $([ ${#CLIID} -eq 64 ]; echo $?)
FH="$W/fakehome"; mkdir -p "$FH"
probe "$FH" pair --server $URL --code "$(code)" --name unused --insecure > /dev/null
probe "$FH" host-fake --name net-host --code "$(code)" --trust "$CLIID" --seconds 70 > "$W/fakehost.log" 2>&1 &
FAKE_PID=$!
for i in $(seq 1 40); do cli list 2>/dev/null | grep -q "online.*net-host" && break; sleep 1; done
NID=$(cli list | awk '/net-host/ {print $4}')
check "net host online and listed for the swift viewer" $([ ${#NID} -eq 64 ]; echo $?)

cli watch "$NID" --out "$W/frames-b" --count 2 --seconds 12 > "$W/b-watch.log" 2>&1; cat "$W/b-watch.log"
check "B: swift viewer receives frames" $(contains "$W/b-watch.log" "desktop=Default")
check "B: swift viewer decodes the .NET hello" $(contains "$W/b-watch.log" "Control, Clipboard, Files")
echo "payload from swift" > "$W/up-b.txt"
( cd "$W" && cli control "$NID" --move 1000,2000 --key 65 --clip "clip from swift" --upload "$W/up-b.txt" --list-share --download hello.txt --wait 2 > "$W/b-control.log" 2>&1 ); cat "$W/b-control.log"
sleep 2
check "B: mouse move reached the .NET host" $(contains "$W/fakehost.log" "INPUT MouseMove x=1000 y=2000")
check "B: key press reached the .NET host" $(contains "$W/fakehost.log" "INPUT KeyDown x=0 y=0 a1=65")
check "B: clipboard reached the .NET host" $(contains "$W/fakehost.log" "CLIPBOARD set: clip from swift")
check "B: clipboard of the .NET host reached the swift viewer" $(contains "$W/b-control.log" "clipboard from host: fake host clipboard")
check "B: upload stored by the .NET host" $(contains "$W/fakehost.log" "Received file 'up-b.txt'")
check "B: shared file downloaded by the swift viewer, identical" $([ "$(cat "$W/downloads/hello.txt" 2>/dev/null)" = "shared by the fake host" ]; echo $?)
kill $FAKE_PID 2>/dev/null; wait $FAKE_PID 2>/dev/null
kill $SERVER_PID 2>/dev/null

echo; echo "FAILED CHECKS: $FAIL"
echo "--- swift host log"; tail -25 "$W/swifthost.log"
[ "$FAIL" -eq 0 ]
