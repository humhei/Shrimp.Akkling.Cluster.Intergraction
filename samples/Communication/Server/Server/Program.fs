// Learn more about F# at http://fsharp.org

open System
open Shared
open Akkling
open Microsoft.FSharp.Linq.RuntimeHelpers
open Shrimp.LiteDB
open LiteDB.FSharp
open LiteDB
open System.IO
[<EntryPoint>]
let main argv =
    File.Delete "test.db"
    
    let bsonMapper =
        FSharpBsonMapper()

    bsonMapper.DbRef<OrderBrand, _> (fun orderBrand -> orderBrand.Products)
    bsonMapper.DbRef<Company, _> (fun company -> company.OrderBrands)

    let db = new LiteDB.LiteDatabase("test.db", bsonMapper)
    let colCompany  = db.GetCollection<Company>()

    colCompany.Insert(
        { Id = ObjectId.NewObjectId() 
          Name = "Company"
          OrderBrands =
            [
                { Id = ObjectId.NewObjectId()
                  Name = "OrderBrand1" 
                  Products = 
                    [|
                        { Id = ObjectId.NewObjectId()  
                          Name ="Hangtag"
                          MyClass = MyClass("MyClass1")
                        }

                        { Id = ObjectId.NewObjectId()  
                          Name ="Sticker"
                          MyClass = MyClass("MyClass2")
                        }
                    |]
                }
                { Id = ObjectId.NewObjectId()
                  Name = "OrderBrand2" 
                  Products = 
                    [|
                        { Id = ObjectId.NewObjectId()  
                          Name ="Hangtag"
                          MyClass = MyClass("MyClass1")
                        }

                        { Id = ObjectId.NewObjectId()  
                          Name ="Sticker"
                          MyClass = MyClass("MyClass2")
                        }
                    |]
                }
            ]
        }

    ) |> ignore

    Server.create(fun ctx ->
        let rec loop i = actor {
            let! msg = ctx.Receive()
            match msg with 
            | ServerMsg.Plus (input1, input2) ->
                ctx.Sender() <! (input1 + input2)
            | ServerMsg.Exp  ->
                failwith "Exception From ServerMsg_Exp"

            | ServerMsg.Expr (expr) ->
                let lambda = LeafExpressionConverter.QuotationToLambdaExpression(expr)
                let r =
                    [ 1 ..100 ]
                    |> List.map (fun m ->
                        lambda.Compile().Invoke(1)
                    )
                    |> List.sum
                ctx.Sender() <! r

            | ServerMsg.Expr2 (expr2) ->
                let a = 1
                ctx.Sender() <! 1

            | ServerMsg.SendUnuxpandedCompany company ->
                ctx.Sender() <! true

            | ServerMsg.GetAllCompanies ->
                let r = 
                    colCompany.FindAll()
                    |> List.ofSeq

                ctx.Sender() <! r

            | ServerMsg.Func (expr) ->
                let r = expr.Invoke(1)
                ctx.Sender() <! r


            | ServerMsg.BsonExpr (bsonExpr) ->
                ctx.Sender() <! 1

            | ServerMsg.BsonValue (bsonValue) ->
                ctx.Sender() <! 1

            | ServerMsg.BsonDoc (bsonValue) ->
                ctx.Sender() <! 1

            | ServerMsg.IHello  hello ->
                ctx.Sender() <! 1
                

        }
        loop 0
    ) |> ignore

    Console.Read()
    printfn "Hello World from F#!"
    0 // return an integer exit code
