// Learn more about F# at http://fsharp.org

open System
open Shared
open Akkling
open System.Threading

[<EntryPoint>]
let main argv =

    //let system = System.create "Hello" <| Configuration.defaultConfig()

    //let actor1 = spawnAnonymous system (props (fun ctx ->
    //    let rec loop () = actor {
    //        let! msg = ctx.Receive()
    //        match msg with 
    //        | ServerMsg.Plus (input1, input2) ->
    //            ctx.Sender() <! (input1 + input2)
    //    }
    //    loop ()
    //))


    let client = Client.create()

    client.WarmUp(fun _ ->
        let result = 
            client <! ServerMsg.Plus (5, 7)

        let result2: int = 
            client <? ServerMsg.Plus (1, 2)
            |> Async.RunSynchronously

        for i = 1 to 1000 do
            let result3: int = 
                client <? ServerMsg.Plus (1, 2)
                |> Async.RunSynchronously

            ()
        ()
    )



    //let result2 = 
    //    client <? ServerMsg.Plus (1, 2)
    //    |> Async.RunSynchronously

    Console.Read()
    printfn "Hello World from F#!"
    0 // return an integer exit code
