module Keyboard

    open GregsStack.InputSimulatorStandard
    open GregsStack.InputSimulatorStandard.Native

    let keyCodes =
        dict[
            "ESCAPE", VirtualKeyCode.ESCAPE;
            "F4", VirtualKeyCode.F4;
            "W", VirtualKeyCode.VK_W;
            "ALT", VirtualKeyCode.LMENU;
            "CTRL", VirtualKeyCode.CONTROL;
        ]

    let sendKeyPress (keyCode : VirtualKeyCode, modifierKeyCode : VirtualKeyCode option) =
        let keyboard = (InputSimulator()).Keyboard
        match modifierKeyCode with
        | Some modifier -> keyboard.ModifiedKeyStroke (modifier, keyCode) |> ignore
        | None -> keyboard.KeyPress keyCode |> ignore
