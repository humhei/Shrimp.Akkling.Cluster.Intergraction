// Learn more about F# at http://fsharp.org

open System
open Shared
open Akkling


[<EntryPoint>]
let main argv =

    Server.create(fun ctx ->
        let rec loop i = actor {
            let! msg = ctx.Receive()
            match msg with 
            | ServerMsg.Plus (input1, input2) ->
                failwith "ServerMsg Plus Error"
            | ServerMsg.Plus2 (input1, input2) ->
                ctx.Sender() <! (input1 + input2)
            | ServerMsg.WarmUp ->
                failwith "WarmUp Error"
        }
        loop 0
    ) |> ignore

    Console.Read()
    printfn "Hello World from F#!"
    0 // return an integer exit code
