// Learn more about F# at http://fsharp.org

open System
open Shared
open Akkling
open System.Threading

[<EntryPoint>]
let main argv =
    let client = Client.create()

    let result: int = 
        client <? ServerMsg.Plus (1, 2)
        |> Async.RunSynchronously

    for i = 1 to 1000 do 
        client <? ServerMsg.Plus (1, 2)
        |> Async.RunSynchronously
        |> ignore

    let b = result = 3
    printfn "Hello World from F#!"
    0 // return an integer exit code
