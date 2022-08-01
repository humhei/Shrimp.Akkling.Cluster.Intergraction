// Learn more about F# at http://fsharp.org

open System
open Shared
open Akkling
open System.Threading
open LiteDB
open LiteDB.FSharp
open LiteDB.FSharp.Query
open Shrimp.Akkling.Cluster.Intergraction

type Record =
    { Id: int 
      Name: string }

[<EntryPoint>]
let main argv =

    let start = DateTime.Now.Millisecond

    let system = System.create "Hello" <| Configuration.defaultConfig()

    let client = Client.create()

    client.WarmUp(fun _ ->
        let a = 1

        //let result1 = 
        //    client <! ServerMsg.Plus (66, 7)

        let result0: int = 
            client <? ServerMsg.Exp
            |> Async.RunSynchronously

        let result2 = 
            [ 1..100 ]
            |> List.map (fun m-> async {
                    let! (v: int) = client <? ServerMsg.Plus (m, 5)
                    return {|Origin = m; New = v|}
                }
            )
            |> Async.Parallel
            |> Async.RunSynchronously

        result2 
        |> Array.iter(fun result -> if result.New - result.Origin <> 5 then failwithf "")

        let result0 = 
            client <? ServerMsg.BsonValue (BsonValue 1)
            |> Async.RunSynchronously

        let result0 = 
            client <? ServerMsg.BsonValue (BsonValue 1)
            |> Async.RunSynchronously

        let result_hello = 
            client <? ServerMsg.IHello (Hello)
            |> Async.RunSynchronously

        let result_hello = 
            client <? ServerMsg.IHello (GoGo)
            |> Async.RunSynchronously

        let result1 = 
            client <! ServerMsg.Plus (66, 7)



        let result3: int = 
            client <? ServerMsg.Plus (1, 2)
            |> Async.RunSynchronously

        let result0: Company list = 
            client <? ServerMsg.GetAllCompanies
            |> Async.RunSynchronously

        let result0 = 
            client <? ServerMsg.SendUnuxpandedCompany (result0)
            |> Async.RunSynchronously



        //let result4: int = 
        //    client <? ServerMsg.Exp
        //    |> Async.RunSynchronously

        let result5: int = 
            client <? ServerMsg.Expr(<@ Func<_, _>(fun a -> 
                let b = a + 6
                let c = b * 2
                c + 1) @>)
            |> Async.RunSynchronously

        let result6: int list = 
            [ 1.. 100 ]
            |> List.map (fun _ ->
                client <? ServerMsg.Expr(<@ Func<_, _>(fun a -> 
                    let b = a + 7
                    let c = b * 3
                    c + 1) @>)
                |> Async.RunSynchronously
            )


        let result7: int = 
            client <? ServerMsg.Expr(<@ Func<_, _>(fun a -> 
                let b = a + 8
                let c = b * 4
                c + 1) @>)
            |> Async.RunSynchronously

        let s = DateTime.Now.Millisecond - start

        System.IO.File.WriteAllText("C:\Users\Jia\Desktop\新建文本文档.txt", s.ToString())

        client.Log.Error(sprintf "ELPASED %A" (DateTime.Now.Millisecond - start))

        //let result6: int = 
        //    client <? ServerMsg.Func(Func<_, _>(fun a -> a + 1))
        //    |> Async.RunSynchronously

        ()
    )



    Console.Read()
    printfn "Hello World from F#!"
    0 // return an integer exit code
