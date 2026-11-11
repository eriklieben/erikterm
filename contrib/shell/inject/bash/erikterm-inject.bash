# erikterm automatic shell integration for bash (shell_integration = "auto"). erikterm starts bash as
# `bash --posix` with ENV pointing here: in that mode an interactive bash reads $ENV and no other startup file
# (login or not), so this script undoes the posix mode, puts the variables back, runs the startup files bash
# itself would have run (see run_startup_files in bash's shell.c), then loads the integration. kitty does
# the same; --rcfile alone wouldn't cover login shells, and the macOS default is one.

if [[ -n ${ERIKTERM_ORIG_ENV+X} ]]; then
  builtin export ENV="$ERIKTERM_ORIG_ENV"
  builtin unset ERIKTERM_ORIG_ENV
else
  builtin unset ENV
fi

# bash --posix defaults HISTFILE to ~/.sh_history unless it's set, so erikterm exports the normal one.
if [[ -n ${ERIKTERM_BASH_UNEXPORT_HISTFILE+X} ]]; then
  builtin export -n HISTFILE
  builtin unset ERIKTERM_BASH_UNEXPORT_HISTFILE
fi

builtin set +o posix
builtin shopt -u inherit_errexit 2>/dev/null # bash 4.4+ turns it on with posix mode; set +o posix doesn't undo that

# Sourced here (as bash runs them), not in a function, so they create globals.
__erikterm_inject_root=${BASH_SOURCE[0]%/*}/..

if builtin shopt -q login_shell; then
  [[ -r /etc/profile ]] && builtin source /etc/profile
  for __erikterm_inject_f in "$HOME/.bash_profile" "$HOME/.bash_login" "$HOME/.profile"; do
    if [[ -r $__erikterm_inject_f ]]; then builtin source "$__erikterm_inject_f"; break; fi
  done
else
  # Distributions build bash with SYS_BASHRC; probing for it isn't possible, these are the known places
  # (Fedora's ~/.bashrc sources /etc/bashrc itself, so that one isn't here).
  for __erikterm_inject_f in /etc/bash.bashrc /etc/bash/bashrc; do
    if [[ -r $__erikterm_inject_f ]]; then builtin source "$__erikterm_inject_f"; break; fi
  done
  [[ -r $HOME/.bashrc ]] && builtin source "$HOME/.bashrc"
fi
builtin unset __erikterm_inject_f

[[ -r $__erikterm_inject_root/erikterm.bash ]] && builtin source "$__erikterm_inject_root/erikterm.bash"
builtin unset __erikterm_inject_root
