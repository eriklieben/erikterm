# erikterm automatic shell integration for fish (shell_integration = "auto"). erikterm starts fish with this
# directory's parent on XDG_DATA_DIRS, so fish reads this file as vendor configuration. It puts XDG_DATA_DIRS
# back (fish has its vendor paths by now) and loads the integration at the first prompt, after config.fish,
# as kitty and ghostty do; the name makes it among the first conf.d files read.

if set -q ERIKTERM_ORIG_XDG_DATA_DIRS
    set -gx XDG_DATA_DIRS "$ERIKTERM_ORIG_XDG_DATA_DIRS"
    set -e ERIKTERM_ORIG_XDG_DATA_DIRS
else
    set -e XDG_DATA_DIRS
end

if status is-interactive
    set -g __erikterm_load_script (status dirname)/../../../erikterm.fish
    function __erikterm_deferred_init --on-event fish_prompt
        functions -e __erikterm_deferred_init
        if test -r "$__erikterm_load_script"
            source "$__erikterm_load_script"
        end
        set -e __erikterm_load_script
    end
end
