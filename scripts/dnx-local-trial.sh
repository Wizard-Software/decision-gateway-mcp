#!/usr/bin/env bash
# Packs the dnx tool packages (pointer + one RID package) from an already published
# Native AOT build, checks their contents against an allowlist, and runs the tool with
# `dotnet dnx` from an isolated local feed. Bash 3.2 compatible (macOS default shell).
#
# Usage: scripts/dnx-local-trial.sh <rid> <published-native> <feed-dir> [dotnet-path]
#   rid               one of: win-x64 linux-x64 linux-arm64 osx-arm64 osx-x64
#   published-native  native file produced by `dotnet publish` for <rid> (same properties as below)
#   feed-dir          absolute path outside the repository, missing or empty; receives the .nupkg files
#   dotnet-path       optional, default: dotnet
#
# Prerequisites: the Host project is restored and published for <rid> with
#   -c Release -r <rid> --no-restore -p:TreatWarningsAsErrors=true -p:TrimmerSingleWarn=false
#
# Exit: 0 = both packages built, contents allowed, native file hashes equal, dnx answered
#       initialize (cold and warm cache), the negative run failed; 1 otherwise.
#
# The script never sets DOTNET_NUGET_SIGNATURE_VERIFICATION; it inherits the caller's value.

set -euo pipefail
export LC_ALL=C

readonly GUARD_SECONDS=120
readonly EXIT_GRACE_SECONDS=15
readonly CSPROJ_REL="src/DecisionGateway.Host/DecisionGateway.Host.csproj"
readonly PACKAGE_ID="DecisionGateway.Mcp"
readonly TOOL_FILE="decision-gateway-mcp"
readonly ALL_RIDS="win-x64 linux-x64 linux-arm64 osx-arm64 osx-x64"
readonly TAB=$'\t'

die() {
  printf 'dnx-local-trial: error: %s\n' "$*" >&2
  exit 1
}

usage() {
  printf 'Usage: %s <rid> <published-native> <feed-dir> [dotnet-path]\n' "$0" >&2
  exit 1
}

is_windows=0
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) is_windows=1 ;;
esac

# Converts a path to the form bash uses (Git Bash on Windows accepts C:\... only via cygpath).
to_unix() {
  if [ "$is_windows" -eq 1 ]; then cygpath -u -- "$1"; else printf '%s\n' "$1"; fi
}

# Converts a path to the form native tools (dotnet, nuget.config) expect.
to_native() {
  if [ "$is_windows" -eq 1 ]; then cygpath -w -- "$1"; else printf '%s\n' "$1"; fi
}

# ---------------------------------------------------------------------------
# Temporary paths and cleanup: the EXIT trap removes only paths created by mktemp.
# ---------------------------------------------------------------------------
tmp_paths=()
active_pid=""
active_stop_flag=""

kill_group() {
  local pid="$1"
  if [ "$is_windows" -eq 1 ]; then
    local winpid=""
    if [ -r "/proc/${pid}/winpid" ]; then winpid="$(cat "/proc/${pid}/winpid")"; fi
    if [ -n "$winpid" ]; then taskkill //PID "$winpid" //T //F > /dev/null 2>&1 || true; fi
    kill -KILL "$pid" 2> /dev/null || true
  else
    kill -KILL -- "-${pid}" 2> /dev/null || kill -KILL "$pid" 2> /dev/null || true
  fi
}

cleanup() {
  if [ -n "$active_stop_flag" ]; then : > "$active_stop_flag" 2> /dev/null || true; fi
  if [ -n "$active_pid" ]; then kill_group "$active_pid"; fi
  local p
  for p in ${tmp_paths[@]+"${tmp_paths[@]}"}; do
    rm -rf -- "${p:?}"
  done
}
trap cleanup EXIT

tmp_base="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
tmp_base="$(to_unix "$tmp_base")"
tmp_base="${tmp_base%/}"

# new_tmp_dir <var>: creates a directory with mktemp, records it for cleanup, assigns it to <var>.
new_tmp_dir() {
  local d
  d="$(mktemp -d "${tmp_base}/dnxtrial.XXXXXX")"
  [ -n "$d" ] && [ -d "$d" ] || die "mktemp failed"
  tmp_paths+=("$d")
  eval "$1=\"\$d\""
}

# ---------------------------------------------------------------------------
# Argument validation
# ---------------------------------------------------------------------------
if [ "$#" -lt 3 ] || [ "$#" -gt 4 ]; then usage; fi

rid="$1"
native_arg="$2"
feed_arg="$3"
dotnet_arg="${4:-dotnet}"

case "$rid" in
  win-x64 | linux-x64 | linux-arm64 | osx-arm64 | osx-x64) ;;
  *) die "rid must be one of: ${ALL_RIDS}" ;;
esac

ext=""
if [ "$rid" = "win-x64" ]; then ext=".exe"; fi

case "$native_arg" in
  -* | "") die "published-native must be a path that does not start with '-'" ;;
esac
native="$(to_unix "$native_arg")"
if [ ! -f "$native" ] || [ ! -s "$native" ]; then
  die "published-native is not an existing, non-empty regular file: ${native_arg}"
fi

case "$feed_arg" in
  -* | "") die "feed-dir must be a path that does not start with '-'" ;;
esac
abs_re='^(/|[A-Za-z]:[\\/])'
if [[ ! "$feed_arg" =~ $abs_re ]]; then die "feed-dir must be an absolute path: ${feed_arg}"; fi
unsafe_re='[&<>"'"'"']'
if [[ "$feed_arg" =~ $unsafe_re ]]; then die "feed-dir must not contain XML special characters"; fi
feed="$(to_unix "$feed_arg")"
feed="${feed%/}"

if [[ "$dotnet_arg" == */* ]] || [[ "$dotnet_arg" == *\\* ]]; then
  dotnet="$(to_unix "$dotnet_arg")"
  if [ ! -x "$dotnet" ]; then die "dotnet-path is not executable: ${dotnet_arg}"; fi
else
  dotnet="$(command -v -- "$dotnet_arg" || true)"
  if [ -z "$dotnet" ]; then die "dotnet-path not found on PATH: ${dotnet_arg}"; fi
fi

for tool in unzip perl cmp; do
  command -v "$tool" > /dev/null 2>&1 || die "required tool not found: ${tool}"
done
if command -v sha256sum > /dev/null 2>&1; then
  sha256_of() { sha256sum -- "$1" | cut -d ' ' -f 1 | tr -d '\\'; }
elif command -v shasum > /dev/null 2>&1; then
  sha256_of() { shasum -a 256 -- "$1" | cut -d ' ' -f 1; }
else
  die "neither sha256sum nor shasum is available"
fi

script_dir="$(cd "$(dirname "$0")" && pwd -P)"
repo_root="$(cd "${script_dir}/.." && pwd -P)"
csproj="${repo_root}/${CSPROJ_REL}"
[ -f "$csproj" ] || die "project file not found: ${CSPROJ_REL}"

# The feed must be outside the repository, missing or empty.
case "${feed}/" in
  "${repo_root}/"*) die "feed-dir must be outside the repository" ;;
esac
if [ -e "$feed" ]; then
  [ -d "$feed" ] || die "feed-dir exists and is not a directory: ${feed_arg}"
  [ -z "$(ls -A "$feed")" ] || die "feed-dir is not empty: ${feed_arg}"
else
  mkdir -p -- "$feed"
fi
feed="$(cd "$feed" && pwd -P)"
case "${feed}/" in
  "${repo_root}/"*) die "feed-dir resolves inside the repository" ;;
esac
feed_native="$(to_native "$feed")"

# ---------------------------------------------------------------------------
# Version (read from the project, never overridden)
# ---------------------------------------------------------------------------
cd "$repo_root"
version="$("$dotnet" msbuild "$(to_native "$csproj")" -getProperty:Version | tr -d '\r' | tail -n 1)"
version_re='^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'
if [[ ! "$version" =~ $version_re ]]; then die "unexpected project version: ${version}"; fi

pointer_name="${PACKAGE_ID}.${version}.nupkg"
rid_name="${PACKAGE_ID}.${rid}.${version}.nupkg"
pointer_pkg="${feed}/${pointer_name}"
rid_pkg="${feed}/${rid_name}"
pointer_lower="$(printf '%s' "$pointer_name" | tr 'A-Z' 'a-z')"
rid_lower="$(printf '%s' "$rid_name" | tr 'A-Z' 'a-z')"

# ---------------------------------------------------------------------------
# Pack with the same properties as publish (no AOT recompilation, equal hashes)
# ---------------------------------------------------------------------------
pack_props=(-c Release --no-restore -o "$feed_native" -tl:off
  -p:TreatWarningsAsErrors=true -p:TrimmerSingleWarn=false
  -p:CopyOutputSymbolsToPublishDirectory=false)

echo "==> pack RID package (${rid})"
"$dotnet" pack "$(to_native "$csproj")" -r "$rid" "${pack_props[@]}"
echo "==> pack pointer package"
"$dotnet" pack "$(to_native "$csproj")" "${pack_props[@]}"

feed_entries="$(ls -A "$feed")"
expected_entries="$(printf '%s\n%s\n' "$pointer_name" "$rid_name" | sort)"
if [ "$(printf '%s\n' "$feed_entries" | sort)" != "$expected_entries" ]; then
  printf 'feed contents:\n%s\n' "$feed_entries" >&2
  die "feed must contain exactly ${pointer_name} and ${rid_name}"
fi

# ---------------------------------------------------------------------------
# Content checks: allowlist, fail-closed
# ---------------------------------------------------------------------------
psmdcp_re='^package/services/metadata/core-properties/[0-9A-Za-z]+\.psmdcp$'

# check_entries <package> <kind> <required...>; kind = pointer | rid
check_entries() {
  local pkg="$1" kind="$2"
  shift 2
  local names name req found allowed status=0
  names="$(unzip -Z1 "$pkg" | tr -d '\r')"
  for req in "$@"; do
    found=0
    while IFS= read -r name; do
      if [ "$name" = "$req" ]; then found=1; break; fi
    done <<< "$names"
    if [ "$found" -eq 0 ]; then
      printf 'dnx-local-trial: missing required entry in %s: %s\n' "$(basename "$pkg")" "$req" >&2
      status=1
    fi
  done
  while IFS= read -r name; do
    allowed=0
    case "$name" in
      "[Content_Types].xml" | "_rels/.rels" | ".mcp/server.json") allowed=1 ;;
    esac
    if [[ "$name" =~ $psmdcp_re ]]; then allowed=1; fi
    if [ "$kind" = "pointer" ]; then
      case "$name" in
        "${PACKAGE_ID}.nuspec" | "tools/any/any/DotnetToolSettings.xml") allowed=1 ;;
      esac
    else
      case "$name" in
        "${PACKAGE_ID}.${rid}.nuspec" | \
          "tools/any/${rid}/${TOOL_FILE}${ext}" | \
          "tools/any/${rid}/DotnetToolSettings.xml" | \
          "tools/any/${rid}/DecisionGateway.Core.pdb" | \
          "tools/any/${rid}/DecisionGateway.Providers.pdb") allowed=1 ;;
      esac
    fi
    if [ "$allowed" -eq 0 ]; then
      printf 'dnx-local-trial: entry not in allowlist in %s: %s\n' "$(basename "$pkg")" "$name" >&2
      status=1
    fi
  done <<< "$names"
  return "$status"
}

echo "==> check package contents"
contents_ok=1
check_entries "$pointer_pkg" pointer \
  "${PACKAGE_ID}.nuspec" "tools/any/any/DotnetToolSettings.xml" ".mcp/server.json" \
  "[Content_Types].xml" "_rels/.rels" || contents_ok=0
check_entries "$rid_pkg" rid \
  "${PACKAGE_ID}.${rid}.nuspec" "tools/any/${rid}/${TOOL_FILE}${ext}" \
  "tools/any/${rid}/DotnetToolSettings.xml" "[Content_Types].xml" "_rels/.rels" || contents_ok=0

settings_xml="$(unzip -p "$pointer_pkg" "tools/any/any/DotnetToolSettings.xml" | tr -d '\r')" || settings_xml=""
for r in $ALL_RIDS; do
  if ! printf '%s\n' "$settings_xml" | grep -Eq "<RuntimeIdentifierPackage [^>]*RuntimeIdentifier=\"${r}\""; then
    printf 'dnx-local-trial: pointer DotnetToolSettings.xml has no RuntimeIdentifierPackage for %s\n' "$r" >&2
    contents_ok=0
  fi
done
[ "$contents_ok" -eq 1 ] || die "package content check failed"

# ---------------------------------------------------------------------------
# Native file hash: published file vs file inside the RID package
# ---------------------------------------------------------------------------
new_tmp_dir work
unzip -p "$rid_pkg" "tools/any/${rid}/${TOOL_FILE}${ext}" > "${work}/packed-native"
sha_published="$(sha256_of "$native")"
sha_packed="$(sha256_of "${work}/packed-native")"
rm -f -- "${work}/packed-native"
if [ "$sha_published" = "$sha_packed" ]; then sha_result="equal"; else sha_result="DIFFERENT"; fi

# ---------------------------------------------------------------------------
# Isolated dnx runs
# ---------------------------------------------------------------------------
write_nuget_config() {
  local dir="$1" source_dir="$2"
  cat > "${dir}/nuget.config" << EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$(to_native "$source_dir")" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
  <fallbackPackageFolders>
    <clear />
  </fallbackPackageFolders>
</configuration>
EOF
}

# Environment scrubbing: provider keys and app configuration never reach the server.
env_unset=(-u JEV_API_KEY -u OPENAI_API_KEY)
while IFS= read -r var_name; do
  case "$var_name" in
    DecisionGateway__* | Logging__*) env_unset+=(-u "$var_name") ;;
  esac
done <<< "$(compgen -e)"

hires_now() { perl -MTime::HiRes=time -e 'printf("%.6f\n", time)'; }

init_line='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"dnx-local-trial","version":"1.0.0"}}}'
id1_re='"id"[[:space:]]*:[[:space:]]*1[,}[:space:]]'

# run_dnx <workdir> <source-dir> <cache-dir> <out> <err>
# Sets: run_rc, run_seconds (time to the "id":1 line, empty if none), run_timed_out.
run_dnx() {
  local wd="$1" src="$2" cache="$3" out="$4" err="$5"
  local stop_flag="${wd}/stop" start end pid i
  rm -f -- "$stop_flag"
  : > "$out"
  : > "$err"
  run_rc=0
  run_seconds=""
  run_timed_out=0
  active_stop_flag="$stop_flag"

  start="$(hires_now)"
  # Plain pipe: the writer keeps stdin open until the reader saw the "id":1 line.
  {
    printf '%s\n' "$init_line"
    i=0
    while [ ! -e "$stop_flag" ] && [ "$i" -lt $(((GUARD_SECONDS + EXIT_GRACE_SECONDS) * 20)) ]; do
      sleep 0.05
      i=$((i + 1))
    done
  } | (cd "$wd" && exec perl -e 'eval { setpgrp(0, 0) }; exec { $ARGV[0] } @ARGV or die "exec failed: $!\n"' -- \
    env "${env_unset[@]}" NUGET_PACKAGES="$(to_native "$cache")" \
    "$dotnet" dnx "${PACKAGE_ID}@${version}" --yes \
    --configfile "$(to_native "${wd}/nuget.config")" --source "$(to_native "$src")") \
    > "$out" 2> "$err" &
  pid=$!
  active_pid="$pid"

  local deadline=$((SECONDS + GUARD_SECONDS))
  while :; do
    if grep -Eq "$id1_re" "$out"; then
      end="$(hires_now)"
      run_seconds="$(perl -e 'printf("%.3f", $ARGV[1] - $ARGV[0])' "$start" "$end")"
      break
    fi
    if ! kill -0 "$pid" 2> /dev/null; then
      if grep -Eq "$id1_re" "$out"; then
        end="$(hires_now)"
        run_seconds="$(perl -e 'printf("%.3f", $ARGV[1] - $ARGV[0])' "$start" "$end")"
      fi
      break
    fi
    if [ "$SECONDS" -ge "$deadline" ]; then
      run_timed_out=1
      kill_group "$pid"
      break
    fi
    sleep 0.02
  done

  # Close stdin, give the server time to exit, then make sure the whole group is gone.
  : > "$stop_flag"
  local grace=$((SECONDS + EXIT_GRACE_SECONDS))
  while kill -0 "$pid" 2> /dev/null && [ "$SECONDS" -lt "$grace" ]; do sleep 0.05; done
  if kill -0 "$pid" 2> /dev/null; then kill_group "$pid"; fi
  wait "$pid" || run_rc=$?
  # Remove any child the tool runner left behind in the group (the leader is already reaped).
  if [ "$is_windows" -eq 0 ]; then kill -KILL -- "-${pid}" 2> /dev/null || true; fi
  active_pid=""
  active_stop_flag=""
}

# Checks stdout: every line must be JSON and one must be the initialize result with serverInfo.
# Prints "OK<TAB>version", "NONJSON<TAB>line" or "NORESPONSE".
check_stdout() {
  perl -MJSON::PP -e '
    my $file = shift;
    open(my $fh, "<", $file) or die "cannot read $file\n";
    my ($ok, $ver) = (0, "");
    my $json = JSON::PP->new->allow_nonref;
    while (my $line = <$fh>) {
      $line =~ s/\r?\n\z//;
      my $d = eval { $json->decode($line) };
      if ($@ || !defined $d) { print "NONJSON\t", substr($line, 0, 300), "\n"; exit 0; }
      if (ref $d eq "HASH" && defined $d->{id} && !ref $d->{id} && $d->{id} eq "1"
          && ref $d->{result} eq "HASH" && ref $d->{result}{serverInfo} eq "HASH") {
        $ok = 1;
        $ver = defined $d->{result}{serverInfo}{version} ? $d->{result}{serverInfo}{version} : "";
      }
    }
    print $ok ? "OK\t$ver\n" : "NORESPONSE\n";
  ' "$1"
}

# Checks the isolated cache: exactly the two package folders, one version each, and every
# .nupkg inside (the canonical lower-case file and the <Id>.nupkg copy the tool installer
# keeps) identical to the feed file.
check_cache() {
  local cache="$1" entries expected f status=0 rel feed_file
  local pointer_seen=0 rid_seen=0
  entries="$(ls -A "$cache" | sort)"
  expected="$(printf '%s\n%s\n' "decisiongateway.mcp" "decisiongateway.mcp.${rid}" | sort)"
  if [ "$entries" != "$expected" ]; then
    printf 'dnx-local-trial: cache must hold exactly the two packages; found:\n%s\n' "$entries" >&2
    status=1
  fi
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    rel="${f#"${cache}/"}"
    case "$rel" in
      "decisiongateway.mcp/${version}/${pointer_lower}")
        feed_file="$pointer_pkg"
        pointer_seen=1
        ;;
      "decisiongateway.mcp/${version}/${PACKAGE_ID}.nupkg") feed_file="$pointer_pkg" ;;
      "decisiongateway.mcp.${rid}/${version}/${rid_lower}")
        feed_file="$rid_pkg"
        rid_seen=1
        ;;
      "decisiongateway.mcp.${rid}/${version}/${PACKAGE_ID}.${rid}.nupkg") feed_file="$rid_pkg" ;;
      *)
        printf 'dnx-local-trial: unexpected package file in cache: %s\n' "$rel" >&2
        status=1
        continue
        ;;
    esac
    if ! cmp -s "$f" "$feed_file"; then
      printf 'dnx-local-trial: cached package differs from the feed: %s\n' "$rel" >&2
      status=1
    fi
  done <<< "$(find "$cache" -type f -name '*.nupkg')"
  if [ "$pointer_seen" -ne 1 ] || [ "$rid_seen" -ne 1 ]; then
    printf 'dnx-local-trial: cache lacks %s or %s\n' "$pointer_lower" "$rid_lower" >&2
    status=1
  fi
  if [ "$status" -ne 0 ]; then
    (cd "$cache" && find . -mindepth 1 | sort) >&2
  fi
  return "$status"
}

dump_err() {
  printf -- '--- dnx stderr (%s) ---\n' "$1" >&2
  cat "$2" >&2 || true
  printf -- '--- end ---\n' >&2
}

new_tmp_dir dnx_dir
new_tmp_dir cache_dir
write_nuget_config "$dnx_dir" "$feed"
[ -z "$(ls -A "$cache_dir")" ] || die "isolated package cache is not empty"

declare_result() {
  # declare_result <label> ; uses run_* and the out/err files of the current run
  local label="$1" out="$2" err="$3" verdict
  if [ "$run_timed_out" -eq 1 ]; then
    dump_err "$label" "$err"
    die "${label}: dnx did not answer within ${GUARD_SECONDS}s"
  fi
  verdict="$(check_stdout "$out")"
  case "$verdict" in
    OK*) server_version="${verdict#OK${TAB}}" ;;
    NONJSON*)
      dump_err "$label" "$err"
      printf 'dnx-local-trial: non-JSON line on stdout (%s): %s\n' "$label" "${verdict#NONJSON${TAB}}"
      die "${label}: stdout is not pure JSON-RPC (partial block of the dnx channel)"
      ;;
    *)
      dump_err "$label" "$err"
      die "${label}: no initialize response with serverInfo (dnx exit code ${run_rc})"
      ;;
  esac
  [ -n "$run_seconds" ] || die "${label}: no timing recorded"
  check_cache "$cache_dir" || die "${label}: isolated cache check failed"
}

server_version=""
echo "==> dnx run 1 (cold cache)"
run_dnx "$dnx_dir" "$feed" "$cache_dir" "${dnx_dir}/out1" "${dnx_dir}/err1"
declare_result "cold run" "${dnx_dir}/out1" "${dnx_dir}/err1"
cold_seconds="$run_seconds"
cold_version="$server_version"

echo "==> dnx run 2 (warm cache)"
run_dnx "$dnx_dir" "$feed" "$cache_dir" "${dnx_dir}/out2" "${dnx_dir}/err2"
declare_result "warm run" "${dnx_dir}/out2" "${dnx_dir}/err2"
warm_seconds="$run_seconds"

# Negative run: pointer package only, fresh cache; dnx must fail and name only the local feed.
echo "==> dnx negative run (pointer package only)"
new_tmp_dir neg_feed
new_tmp_dir neg_cache
new_tmp_dir neg_dir
cp -- "$pointer_pkg" "${neg_feed}/"
write_nuget_config "$neg_dir" "$neg_feed"
run_dnx "$neg_dir" "$neg_feed" "$neg_cache" "${neg_dir}/out" "${neg_dir}/err"
neg_rc="$run_rc"
if [ "$run_timed_out" -eq 1 ]; then
  dump_err "negative run" "${neg_dir}/err"
  die "negative run: dnx did not finish within ${GUARD_SECONDS}s"
fi
if [ "$neg_rc" -eq 0 ]; then
  die "negative run: dnx exited 0 without the RID package; feed isolation is not effective"
fi
neg_msg="$(cat "${neg_dir}/out" "${neg_dir}/err" | tr -d '\r' | grep -v '^[[:space:]]*$' | head -n 5 || true)"
if printf '%s\n' "$neg_msg" | grep -Eiq 'https?://'; then
  printf '%s\n' "$neg_msg" >&2
  die "negative run: the error names a remote source; feed isolation is not effective"
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
size_of() { wc -c < "$1" | tr -d ' '; }
echo ""
echo "== dnx-local-trial summary (${rid}) =="
echo "version:            ${version}"
echo "pointer package:    ${pointer_name} ($(size_of "$pointer_pkg") bytes)"
echo "RID package:        ${rid_name} ($(size_of "$rid_pkg") bytes)"
echo "sha256 published:   ${sha_published}"
echo "sha256 packed:      ${sha_packed}"
echo "sha256 comparison:  ${sha_result}"
echo "first dnx run (download + extract + start), single sample, outside the startup target: ${cold_seconds} s"
echo "second dnx run (warm cache: version resolution + start), single sample: ${warm_seconds} s"
echo "serverInfo.version: ${cold_version} (assembly version, not compared with the package version)"
echo "negative run exit:  ${neg_rc}"
echo "negative run error: $(printf '%s' "$neg_msg" | head -n 1 | cut -c 1-200)"

if [ "$sha_result" != "equal" ]; then
  die "native file hashes differ between publish output and RID package"
fi
echo "result: OK"
