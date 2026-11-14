#!/bin/bash
# Usage: pc.sh '<powershell command>'   Runs it on the Windows PC via SSH inside an iTerm2 window (which has Local Network access).
SP="${NODRIVE_SCRATCH:-/tmp}"
CMD="$1"
ID=$(date +%s%N)
OUT="$SP/pc-$ID.out"
RUN="$SP/pc-$ID.sh"
printf '%s\n' '#!/bin/bash' "ssh -o BatchMode=yes -o ConnectTimeout=30 ${NODRIVE_PC:-<PC_USER>@<PC_IP>} '$CMD' > '$OUT' 2>&1" "echo \"__EXIT:\$?\" >> '$OUT'" > "$RUN"
chmod +x "$RUN"
osascript <<OSA >/dev/null 2>&1
tell application "iTerm2"
  set w to (create window with default profile command "/bin/bash $RUN")
end tell
OSA
for i in $(seq 1 120); do grep -q '^__EXIT:' "$OUT" 2>/dev/null && break; sleep 1; done
grep -v -E "post-quantum|store now|openssh.com/pq" "$OUT" 2>/dev/null | grep -v '^$'
rm -f "$RUN"
