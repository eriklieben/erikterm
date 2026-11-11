# erikterm automatic shell integration for zsh (shell_integration = "auto"). erikterm starts zsh with ZDOTDIR
# pointing at this directory, so this runs instead of the user's own .zshenv and is the only thing that
# changes: it puts ZDOTDIR back, runs the user's real .zshenv, and loads the integration at the first prompt,
# after .zshrc and .zlogin, like kitty and ghostty do.
#
# zsh reads this file before any other of its rc files, so nothing of the user's sees erikterm's ZDOTDIR,
# and a program started from here on inherits the user's own value (or none).
#
# Some of what /etc/zshenv does can reach us, aliases included, so everything that can be quoted is.

if [[ -n ${ERIKTERM_ORIG_ZDOTDIR+X} ]]; then
  'builtin' 'export' ZDOTDIR="$ERIKTERM_ORIG_ZDOTDIR"
  'builtin' 'unset' 'ERIKTERM_ORIG_ZDOTDIR'
else
  'builtin' 'unset' 'ZDOTDIR'
fi

# The integration's directory; before the user's .zshenv can touch anything.
'builtin' 'typeset' __erikterm_root=${${(%):-%x}:A:h:h}

{
  # zsh treats an unset ZDOTDIR as $HOME and skips an unreadable rc file; so do we.
  'builtin' 'typeset' __erikterm_zshenv=${ZDOTDIR-~}/.zshenv
  [[ ! -r $__erikterm_zshenv ]] || 'builtin' 'source' '--' "$__erikterm_zshenv"
} always {
  'builtin' 'unset' '__erikterm_zshenv'
  if [[ -o interactive ]]; then
    # At the first prompt the user's .zshrc has run: the prompt exists and the framework's hooks are in place.
    __erikterm_load_script=$__erikterm_root/erikterm.zsh
    function __erikterm_deferred_init() {
      builtin emulate -L zsh
      local script=$__erikterm_load_script
      builtin unset __erikterm_load_script
      local -i others=$(( ${#precmd_functions} - 1 + ${+functions[precmd]} ))
      precmd_functions=(${precmd_functions:#__erikterm_deferred_init})
      local -i sourced_by_user=${+__erikterm_integration}
      [[ -r $script ]] && builtin source -- "$script"
      # Already sourced by the user's rc (its hooks do the rest), or the script bailed out (real tmux): nothing more to do.
      if (( ! sourced_by_user && ${+functions[__erikterm_osc7]} )); then
        __erikterm_osc7
        if ! __erikterm_prompt_marks_by_framework; then
          if (( others )); then
            # We run before the other hooks of this first prompt, so a framework that rebuilds PS1 in its
            # precmd (oh-my-posh, pure, p10k) would erase anything added to PS1 now. Mark the start directly
            # (idempotent if PS1 turns out to keep it) and leave only B to PS1, which is lost only then;
            # from the second prompt on __erikterm_precmd_last wraps PS1 after all of them.
            printf '\e]133;A\e\\'
            [[ $PS1 == *${__erikterm_B} ]] || PS1="${PS1}${__erikterm_B}"
          else
            __erikterm_wrap_prompt
          fi
        fi
      fi
      builtin unfunction __erikterm_deferred_init
    }
    'builtin' 'typeset' -ag precmd_functions
    precmd_functions+=(__erikterm_deferred_init)
  fi
  'builtin' 'unset' '__erikterm_root'
}
