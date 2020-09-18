// Learn more about F# at http://fsharp.org

open System
open Shared
open Akkling

type Hello() =
    member x.Sender() = ""

type MM() =
    inherit Hello()
    member x.Sender() = 6

    static member (<<!) (mm: MM, v) =
        let c = box v
        printf "NN"

[<EntryPoint>]
let main argv =

    let system = System.create "SSS" <| Configuration.defaultConfig()

    let mm = spawnAnonymous system (props (fun ctx ->
        let rec loop () = actor {
            let! msg = ctx.Receive()
            match msg with 
            | "Test" -> failwith "Good"
            | _ -> failwith "Invalid token"
        }

        loop ()
    ))

    Server.create(fun ctx ->
        let rec loop i = actor {
            let! msg = ctx.Receive()
            match msg with 
            | ServerMsg.Plus (input1, input2) ->
                try 
                    let! result = mm <? "Test"
                    ctx.Sender() <! result
                    return! loop (i + 1)

                with ex ->
                    failwith ""
                //ctx.Sender() <! (input1 + input2)
            | ServerMsg.Plus2 (input1, input2) ->
                ctx.Sender() <! (input1 + input2)

            | ServerMsg.WarmUp ->
                failwith "Hello"
        }
        loop 0
    ) |> ignore

    let mm = MM()
    

    Console.Read()
    printfn "Hello World from F#!"
    0 // return an integer exit code
