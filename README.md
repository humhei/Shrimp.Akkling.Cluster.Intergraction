# Shrimp.Akkling.Cluster.Intergraction
Build upon on Akkling for client and server communication

Has below features:
1: Message and Callback message
2: Warmup Message(Client send first message to server when connection is linked)
3: Cluster load balance(This feature is not completed, now it's only a communication between client and server) 

# Samples
Configurate your server seed point in `Shared.reference.conf`
If necessary, Reconfigurate it in Client And Server 

## Shared library
```fsharp
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

/// do more typed akka configurations here
/// e.g: logger, serializer
let private configurationSetParams (args: ClusterConfigBuildingArgs) =
    {args with ``akka.loggers`` = Loggers (Set.ofList [Logger.Default])}

[<RequireQualifiedAccess>]
type ServerMsg =
    | Plus of input1: int * input2: int
    | Plus2 of input1: int * input2: int
    | WarmUp


[<RequireQualifiedAccess>]
module Client =
    let create() =
        Client<unit, ServerMsg>(SYSTEM_NAME, CLIENT_ROLE, SERVER_ROLE, 0, port, Behaviors.ignore, configurationSetParams)

[<RequireQualifiedAccess>]
module Server =
    let create(receive) =
        Server<unit, ServerMsg>(SYSTEM_NAME, SERVER_ROLE, CLIENT_ROLE, port, port, configurationSetParams , receive)
```

## Client Side 
```fsharp
// Learn more about F# at http://fsharp.org

open System
open Shared
open Akkling
open System.Threading

[<EntryPoint>]
let main argv =

    let client = Client.create()

    client.WarmUp(fun _ ->

        let result3 = 
            client <! ServerMsg.Plus (66, 7)

        let result2: int = 
            client <? ServerMsg.Plus (1, 5)
            |> Async.RunSynchronously

        let result3: int = 
            client <? ServerMsg.Plus (1, 2)
            |> Async.RunSynchronously
        ()
    )


    Console.Read()
    printfn "Hello World from F#!"
    0 // return an integer exit code

```

## Server Side
```fsharp
// Learn more about F# at http://fsharp.org

open System
open Shared
open Akkling


[<EntryPoint>]
let main argv =
    Server.create(fun ctx ->
        let rec loop i = actor {
            let! msg = ctx.Receive()
            match msg with 
            | ServerMsg.Plus (input1, input2) ->
                failwith "ServerMsg Plus Error"
            | ServerMsg.Plus2 (input1, input2) ->
                ctx.Sender() <! (input1 + input2)
            | ServerMsg.WarmUp ->
                failwith "WarmUp Error"
        }
        loop 0
    ) |> ignore

    Console.Read()
    printfn "Hello World from F#!"
    0 // return an integer exit code

```
##

Stable | Prerelease
--- | ---
[![NuGet Badge](https://buildstats.info/nuget/Shrimp.Akkling.Cluster.Intergraction)](https://www.nuget.org/packages/Shrimp.Akkling.Cluster.Intergraction/) | [![NuGet Badge](https://buildstats.info/nuget/Shrimp.Akkling.Cluster.Intergraction?includePreReleases=true)](https://www.nuget.org/packages/Shrimp.Akkling.Cluster.Intergraction/)


MacOS/Linux | Windows
--- | ---
[![CircleCI](https://circleci.com/gh/myName/Shrimp.Akkling.Cluster.Intergraction.svg?style=svg)](https://circleci.com/gh/myName/Shrimp.Akkling.Cluster.Intergraction) | [![Build status](https://ci.appveyor.com/api/projects/status/0qnls95ohaytucsi?svg=true)](https://ci.appveyor.com/project/myName/Shrimp.Akkling.Cluster.Intergraction)
[![Build History](https://buildstats.info/circleci/chart/myName/Shrimp.Akkling.Cluster.Intergraction)](https://circleci.com/gh/myName/Shrimp.Akkling.Cluster.Intergraction) | [![Build History](https://buildstats.info/appveyor/chart/myName/Shrimp.Akkling.Cluster.Intergraction)](https://ci.appveyor.com/project/myName/Shrimp.Akkling.Cluster.Intergraction)

---