# Source this file from any directory inside the repository in bash or zsh.
_autobodyservice_activate_dotnet() {
    local project_root dotnet_root sdk_version runtimes

    if ! project_root="$(git rev-parse --show-toplevel 2>/dev/null)"; then
        printf '%s\n' 'Run source scripts/activate-dotnet.sh from the AutoBodyService repository.' >&2
        return 1
    fi
    dotnet_root="$project_root/.dotnet"
    if [ ! -x "$dotnet_root/dotnet" ] || [ ! -f "$project_root/global.json" ]; then
        printf '%s\n' 'The project-local .NET SDK is missing. See the .NET SDK section of README.md.' >&2
        return 1
    fi
    if ! sdk_version="$("$dotnet_root/dotnet" --version)" || ! printf '%s\n' "$sdk_version" | awk -F. 'NF == 3 && $1 == 10 && $2 == 0 && $3 >= 403 && $3 < 500 { ok = 1 } END { exit !ok }'; then
        printf '%s\n' 'The project-local .NET SDK must be a stable 10.0.403 or later patch in the 10.0.4xx feature band.' >&2
        return 1
    fi
    if ! runtimes="$("$dotnet_root/dotnet" --list-runtimes)" || ! printf '%s\n' "$runtimes" | awk '
        $1 == "Microsoft.NETCore.App" && $2 ~ /^10\.0\.[0-9]+$/ { split($2, v, "."); if (v[3] >= 14) core = 1 }
        $1 == "Microsoft.AspNetCore.App" && $2 ~ /^10\.0\.[0-9]+$/ { split($2, v, "."); if (v[3] >= 14) aspnet = 1 }
        END { exit !(core && aspnet) }'; then
        printf '%s\n' 'The project-local .NET and ASP.NET Core 10.0.14 or later runtimes are required.' >&2
        return 1
    fi

    export DOTNET_ROOT="$dotnet_root"
    unset DOTNET_ROOT_ARM64 DOTNET_ROOT_X64 DOTNET_ROOT_X86 DOTNET_ROOT_ARM
    case ":$PATH:" in
        ":$dotnet_root:"*) ;;
        *) export PATH="$dotnet_root:$PATH" ;;
    esac
    printf 'Activated project-local .NET SDK %s (%s).\n' "$sdk_version" "$dotnet_root"
}

if _autobodyservice_activate_dotnet; then
    unset -f _autobodyservice_activate_dotnet
else
    unset -f _autobodyservice_activate_dotnet
    return 1
fi
