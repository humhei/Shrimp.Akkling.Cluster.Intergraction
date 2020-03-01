// Learn more about F# at http://fsharp.org

open System
open Shared
open Akkling

[<EntryPoint>]
let main argv =
    Server.create(fun ctx ->
        let rec loop () = actor {
            let! msg = ctx.Receive()
            match msg with 
            | ServerMsg.Plus (input1, input2) ->
                ctx.RespondSafely(fun _ ->
                    box (input1 + input2)
                )
            | ServerMsg.WarmUp ->
                ctx.NotifySafely(fun _ ->
                    ()
                )
        }
        loop ()
    ) |> ignore
    Console.Read()
    printfn "Hello World from F#!"
    0 // return an integer exit code
