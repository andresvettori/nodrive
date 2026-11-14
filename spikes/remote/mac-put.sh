#!/bin/bash
# Usage: put.sh <local-file> <remote C:/ path>   scp via iTerm2 (Local Network permission), waits for completion.
SP="${NODRIVE_SCRATCH:-/tmp}"
ID=$(date +%s%N); OUT="$SP/put-$ID.out"; RUN="$SP/put-$ID.sh"
printf '%s\n' '#!/bin/bash' "scp -q -o BatchMode=yes -o ConnectTimeout=30 '$1' '${NODRIVE_PC:-<PC_USER>@<PC_IP>}:$2' > '$OUT' 2>&1" "echo \"__EXIT:\$?\" >> '$OUT'" > "$RUN"
chmod +x "$RUN"
osascript -e "tell application \"iTerm2\" to create window with default profile command \"/bin/bash $RUN\"" >/dev/null 2>&1
for i in $(seq 1 120); do grep -q '^__EXIT:' "$OUT" 2>/dev/null && break; sleep 1; done
grep -v -E "post-quantum|store now|openssh.com/pq" "$OUT" | grep -v '^$' | tr '\n' ' '; echo; rm -f "$RUN"
