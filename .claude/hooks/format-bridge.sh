#!/usr/bin/env sh

# Best-effort PostToolUse formatter. A formatter problem must never block editing.
payload=$(cat 2>/dev/null) || exit 0

if command -v node >/dev/null 2>&1; then
  file_path=$(printf '%s' "$payload" | node -e '
    let input = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", chunk => { input += chunk; });
    process.stdin.on("end", () => {
      try {
        const value = JSON.parse(input).tool_input?.file_path;
        if (typeof value === "string") process.stdout.write(value);
      } catch {}
    });
  ' 2>/dev/null) || file_path=""
else
  file_path=$(printf '%s' "$payload" | sed -n 's/.*"file_path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)
fi

[ -n "$file_path" ] || exit 0

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" 2>/dev/null && pwd) || exit 0
repo_root=$(CDPATH= cd -- "$script_dir/../.." 2>/dev/null && pwd) || exit 0

if command -v cygpath >/dev/null 2>&1; then
  candidate=$(cygpath -u "$file_path" 2>/dev/null) || exit 0
else
  candidate=$(printf '%s' "$file_path" | tr '\\' '/')
fi

case "$candidate" in
  bridge/*) target="$repo_root/$candidate" ;;
  "$repo_root"/bridge/*) target="$candidate" ;;
  *) exit 0 ;;
esac

target_dir=$(CDPATH= cd -- "$(dirname -- "$target")" 2>/dev/null && pwd) || exit 0
target="$target_dir/$(basename -- "$target")"

case "$target" in
  "$repo_root"/bridge/*) ;;
  *) exit 0 ;;
esac

case "$target" in
  *package-lock.json) exit 0 ;;
esac

case "$target" in
  *.ts|*.js|*.json|*.md) ;;
  *) exit 0 ;;
esac

[ -f "$target" ] || exit 0
[ -d "$repo_root/bridge/node_modules" ] || exit 0
command -v node >/dev/null 2>&1 || exit 0
command -v timeout >/dev/null 2>&1 || exit 0
prettier="$repo_root/bridge/node_modules/.bin/prettier"
[ -f "$prettier" ] || exit 0

(cd "$repo_root/bridge" && timeout 5s "$prettier" --write "$target" >/dev/null 2>&1) || true
exit 0
