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

    let b = result = 3
    printfn "Hello World from F#!"
    0 // return an integer exit code
