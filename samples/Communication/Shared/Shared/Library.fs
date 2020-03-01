module Shared

open Akka.Configuration
open Shrimp.Akkling.Cluster.Intergraction
open Akkling
open Shrimp.Akkling.Cluster.Intergraction.Configuration

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

[<RequireQualifiedAccess>]
type ServerMsg =
    | Plus of input1: int * input2: int
    | WarmUp


[<RequireQualifiedAccess>]
module Client =
    let create() =
        Client<unit, ServerMsg>(SYSTEM_NAME, CLIENT_ROLE, SERVER_ROLE, 0, port, Behaviors.ignore, configurationSetParams)

[<RequireQualifiedAccess>]
module Server =
    let create(receive) =
        Server<unit, ServerMsg>(SYSTEM_NAME, SERVER_ROLE, CLIENT_ROLE, port, port, configurationSetParams , receive)