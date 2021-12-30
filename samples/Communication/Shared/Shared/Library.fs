module Shared

open Akka.Configuration
open Shrimp.Akkling.Cluster.Intergraction
open Akkling
open Shrimp.Akkling.Cluster.Intergraction.Configuration
open Microsoft.FSharp.Quotations
open System
open LiteDB

type private AssemblyFinder = AssemblyFinder

let private referenceConfig = 
    ConfigurationFactory.FromResource<AssemblyFinder>("Shared.reference.conf")
    |> Configuration.fallBackByApplicationConf

let [<Literal>] private CLIENT_ROLE = "Client"
let [<Literal>] private SERVER_ROLE = "Server"
let [<Literal>] private SYSTEM_NAME = "Shared"

let private port = referenceConfig.GetInt("Shared.port")

let private configurationSetParams (args: ClusterConfigBuildingArgs) =
    {args with ``akka.loggers`` = Loggers (Set.ofList [Logger.Default])}

type MyClass(name: string) =
    member x.Name = name

type Product =
    { Id: ObjectId 
      Name: string
      MyClass: MyClass
    }

type OrderBrand =
    { Id: ObjectId  
      Name: string
      Products: Product [] }

type Company =
    { Id: ObjectId
      Name: string
      OrderBrands: OrderBrand list }
with 
    override x.ToString() =
        x.Id.ToString() + "name: " + x.Name

type IHello =
    abstract member Name: string

type Hello = Hello with 
    interface IHello with 
        member x.Name = "Hello"

type GoGo = GoGo with 
    interface IHello with 
        member x.Name = "GoGo"

[<RequireQualifiedAccess>]
type ServerMsg =
    | Plus of input1: int * input2: int
    | GetAllCompanies
    | SendUnuxpandedCompany of Company list
    | Exp 
    | Expr of Expr<Func<int, int>>
    | Expr2 of Expr
    | Func of Func<int, int>
    | BsonExpr of BsonExpression
    | BsonValue of BsonValue
    | BsonDoc of BsonDocument
    | IHello of IHello
     
[<RequireQualifiedAccess>]
module Client =
    let create() =
        Client<unit, ServerMsg>(SYSTEM_NAME, CLIENT_ROLE, SERVER_ROLE, 0, port, Behaviors.ignore, configurationSetParams)

[<RequireQualifiedAccess>]
module Server =
    let create(receive) =
        Server<unit, ServerMsg>(SYSTEM_NAME, SERVER_ROLE, CLIENT_ROLE, port, port, configurationSetParams , receive)