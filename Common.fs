module Common

    let mapOption = function
        | true, value -> Some value
        | _ -> None
