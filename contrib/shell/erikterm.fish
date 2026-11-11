# erikterm shell integration for fish — OSC 133 semantic prompts and OSC 7.
#
# fish 4.0+ emits OSC 133 by itself, and fish has always reported its directory (OSC 7) to
# terminals it recognises; this file adds both for older versions. Source it from
# ~/.config/fish/config.fish after your prompt is set up (e.g. after `starship init fish | source`):
#
#   test "$TERM_PROGRAM" = erikterm; and source /path/to/erikterm/contrib/shell/erikterm.fish
#
# Everything is inside an `if`, with no early exit: the file can also be sourced from a function (the automatic
# integration's fish_prompt handler), and what `exit` does there depends on the fish version.

if status is-interactive
    and not set -q __erikterm_integration
    and begin
        not set -q TMUX; or string match -q -- "$ERIKTERM_SOCKET,*" "$TMUX" # real tmux (not the emulation) does nothing
    end
    set -g __erikterm_integration 1

    function __erikterm_osc7 --on-variable PWD
        printf '\e]7;file://%s%s\e\\' $hostname (string escape --style=url -- $PWD)
    end

    set -l major (string split . -- $version)[1]
    if test "$major" -lt 4 # 4.0+: native OSC 133; only the directory is left to report.

        # Output starts; $argv[1] is the command line as typed (sent so erikterm knows it even after a transient prompt).
        function __erikterm_preexec --on-event fish_preexec
            printf '\e]133;C;cmdline_url=%s\e\\' (string escape --style=url -- (string sub --length 2048 -- "$argv[1]"))
        end

        function __erikterm_postexec --on-event fish_postexec
            printf '\e]133;D;%d\e\\' $status
        end

        # Wrap the prompt function (starship's or fish's own) with A … B. The original runs first thing, before any
        # other command, so it sees the command's $status and $pipestatus untouched; its output is captured as is.
        functions -q fish_prompt; and functions -c fish_prompt __erikterm_original_prompt

        function fish_prompt
            if functions -q __erikterm_original_prompt
                set -l prompt (__erikterm_original_prompt | string collect --no-trim-newlines)
                printf '\e]133;A\e\\%s\e]133;B\e\\' "$prompt"
            else
                printf '\e]133;A\e\\> \e]133;B\e\\'
            end
        end
    end

    __erikterm_osc7
end
