// Learn more about F# at http://fsharp.org

open System
open Shared
open Akkling
open System.Threading

[<EntryPoint>]
let main argv =
    let client = Client.create()

    client.WarmUp(fun _ ->
        let result = 
            client <? ServerMsg.Plus (1, 2)

        let result2: int = 
            client <? ServerMsg.Plus (1, 2)
            |> Async.RunSynchronously
        ()
    )



    //let result2 = 
    //    client <? ServerMsg.Plus (1, 2)
    //    |> Async.RunSynchronously

    Console.Read()
    printfn "Hello World from F#!"
    0 // return an integer exit code
