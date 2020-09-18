// Learn more about F# at http://fsharp.org

open System
open Shared
open Akkling
open System.Threading

[<EntryPoint>]
let main argv =

    let system = System.create "Hello" <| Configuration.defaultConfig()

    let client = Client.create()

    client.WarmUp(fun _ ->

        //client <! ServerMsg.WarmUp

        //let m = 
        //    try
        //        client <? ServerMsg.WarmUp
        //        |> Async.RunSynchronously
        //    with ex ->
        //        printfn "%A" ex

        //let result = 
        //    client <! ServerMsg.Plus (5, 7)

        let result3 = 
            client <! ServerMsg.Plus (66, 7)

        let result2: int = 
            client <? ServerMsg.Plus (1, 5)
            |> Async.RunSynchronously

        let result3: int = 
            client <? ServerMsg.Plus (1, 2)
            |> Async.RunSynchronously

        //let m = [
        //    for i = 1 to 1000 do
        //        if i % 2 = 0 
        //        then
        //            client <! ServerMsg.Plus (i, 3)
        //            client <! ServerMsg.Plus (i, 4)
        //            client <! ServerMsg.Plus (i, 5)
        //        else
        //            let result3: int =
        //                client <? ServerMsg.Plus (i, 2)
        //                |> Async.RunSynchronously
        //            yield result3
        //]
        //let c = m
        ()
        
    )



    //let result2 = 
    //    client <? ServerMsg.Plus (1, 2)
    //    |> Async.RunSynchronously

    Console.Read()
    printfn "Hello World from F#!"
    0 // return an integer exit code
