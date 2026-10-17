#!/usr/bin/env bash
# Prints README.md for the ci-screenshots branch: what the branch is, the runs it holds, and a link to every image of the latest run.
# Run from the root of the branch checkout (it reads runs/ and latest/).
set -euo pipefail

echo "# Remotely UI screenshots"
echo
echo "Written by the \`ui\` workflow (.github/workflows/ui.yml). This branch has no source code; it is rewritten as a single commit on each run and holds the five newest runs."
echo "A picture existing does not mean the UI is right: someone has to look at it. Pixel drift against the approved baselines (docs/screenshots/ui on the source branch) is checked by the workflow itself."
echo
echo "## Runs"
echo
echo "| Run | Commit | Branch | ui job |"
echo "|---|---|---|---|"
for run in $(ls -1 runs | sort -t- -k2 -n -r); do
  meta="runs/${run}/run.json"
  [ -f "$meta" ] || continue
  id=$(sed -n 's/.*"run_id":\([0-9]*\).*/\1/p' "$meta")
  sha=$(sed -n 's/.*"commit":"\([0-9a-f]*\)".*/\1/p' "$meta")
  br=$(sed -n 's/.*"branch":"\([^"]*\)".*/\1/p' "$meta")
  res=$(sed -n 's/.*"ui_job_result":"\([^"]*\)".*/\1/p' "$meta")
  echo "| [${run}](runs/${run}) | \`${sha:0:7}\` | ${br} | ${res} |"
done
echo
echo "## Latest run"
echo
for arch in latest/*/; do
  [ -d "$arch" ] || continue
  rid=$(basename "$arch")
  echo "### ${rid}"
  echo
  [ -f "${arch}summary.md" ] && echo "[summary](latest/${rid}/summary.md)" && echo
  for variant in $(find "$arch" -mindepth 1 -maxdepth 1 -type d | sort); do
    v=$(basename "$variant")
    case "$v" in tree|diffs) continue ;; esac
    echo "**${v}**"
    echo
    find "$variant" -name '*.png' | sort | while read -r f; do
      rel="${f#latest/}"
      echo "- [${rel#${rid}/}](latest/${rel})"
    done
    echo
  done
done
