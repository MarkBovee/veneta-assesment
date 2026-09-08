#!/usr/bin/env bash
set -euo pipefail

files=$(rg --files -g '*.cs')

if [[ -z "$files" ]]; then
    exit 0
fi

if rg -n '/// <summary>[^<]+</summary>' $files; then
    echo "XML summaries must use multiline documentation blocks." >&2
    exit 1
fi

dotnet format Veneta.Assessments.slnx --verify-no-changes --verbosity minimal
