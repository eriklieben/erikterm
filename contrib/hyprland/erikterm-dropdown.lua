-- erikterm as a Quake-style drop-down terminal on Hyprland (Lua config, 0.55+).
--
-- Paste into ~/.config/hypr/hyprland.lua (or require() it from there) and put
-- contrib/hyprland/erikterm-dropdown on your PATH. SUPER+F12 then slides a
-- full-width terminal down from the top of the screen and back up again; it
-- keeps running while hidden, so splits and scrollback survive.
--
-- The slide comes from the specialWorkspace animation, e.g.
--   hl.animation({ leaf = "specialWorkspace", enabled = true, speed = 5, bezier = "easeOutQuint", style = "slidevert" })
--
-- size/move are muparser expressions over monitor_w, monitor_h, window_w, ...
-- (the old "100% 45%" percent syntax is not accepted: the rule's size is then
-- silently ignored). Checked against Hyprland 0.57 with --verify-config and a
-- nested instance.

hl.window_rule({
    name      = "erikterm-dropdown",
    match     = { class = "^(erikterm-dropdown)$" },
    float     = true,
    size      = "monitor_w monitor_h*0.45",
    move      = "0 0",
    -- Opens hidden on its own special workspace; the toggle script shows it.
    workspace = "special:dropdown silent",
})

-- Launches the drop-down the first time, then just shows/hides it.
-- (A plain string: a require()d file doesn't see your config's locals.)
hl.bind("SUPER + F12", hl.dsp.exec_cmd("erikterm-dropdown"))
