namespace Shrimp.Akkling.Cluster.Intergraction
open Akkling
open System
open Akka.Actor
open Akka.Cluster
open Akkling.Cluster
open System.Threading
open System.Timers
open Akka.Event
open Extensions
open Akka.Configuration

[<RequireQualifiedAccess>]
module internal Client =

    type private ServerRemovedEvent = ServerRemovedEvent of RemoteActorIdentity
    type private NodeServerIsolatedEvent = NodeServerIsolatedEvent of RemoteActorIdentity

    let private (|ServerUnreachableEvent|_|) (message: obj) =
        match message with 
        | :? ServerRemovedEvent as removeServerEvent ->
            let (ServerRemovedEvent server) = removeServerEvent 
            Some server
        | :? NodeServerIsolatedEvent as isolateNodeServerEvent ->
            let (NodeServerIsolatedEvent server) = isolateNodeServerEvent
            Some server
        | _ -> None


    type private EndpointsUpdatedEvent = EndpointsUpdatedEvent of Map<Address, RemoteActorReachable * RemoteActor<obj>>

    [<RequireQualifiedAccess>]
    module private RemoteActorManager =
        type private Model =
            { Endpoints: Map<Address, RemoteActorReachable * RemoteActor<obj>> }

        let createAgent (seedNodes: string seq) clusterSystem serverRoleName : IActorRef<EndpointMsg> = 
            spawnAnonymous clusterSystem (props (fun ctx ->
                let log = ctx.Log.Value
                let cluster = Cluster.Get(clusterSystem)
                let rec loop (model: Model)  = actor {
                    let! recievedMsg = ctx.Receive()

                    match recievedMsg with 
                    | LifecycleEvent e ->
                        match e with
                        | PreStart ->
                            cluster.Subscribe(untyped ctx.Self, ClusterEvent.InitialStateAsEvents,
                                [| typedefof<ClusterEvent.IMemberEvent> |])
                            log.Info (sprintf "Actor subscribed to Cluster status updates: %O" ctx.Self)
                        | PostStop ->
                            cluster.Unsubscribe(untyped ctx.Self)
                            log.Info (sprintf "Actor unsubscribed from Cluster status updates: %O" ctx.Self)

                        | _ -> return Unhandled

                    | IMemberEvent e ->
                        match e with
                        | MemberJoined m | MemberUp m  ->
                            match e with 
                            | MemberJoined _ -> log.Info (sprintf "[CLIENT] Node joined: %O" m)
                            | MemberUp _ -> log.Info (sprintf "[CLIENT] Node up: %O" m)
                            | _ -> failwith "Invalid token"

                            if m.HasRole serverRoleName then
                                let server = 
                                    { Address = m.Address 
                                      Role = serverRoleName }

                                ctx.Self <! box (EndpointMsg.AddServer server)

                        | MemberLeft m ->
                            log.Info (sprintf "[CLIENT] Node left: %O" m)

                        | MemberExited m ->
                            log.Info (sprintf "[CLIENT] Node exited: %O" m)

                        | MemberRemoved m ->
                            log.Info (sprintf "[CLIENT] Remote Node removed: %O" m)
                            if m.HasRole serverRoleName then
                                ctx.Self <! box (EndpointMsg.RemoveServer m.Address)

                    | :? EndpointMsg as endpointMsg ->
                        match endpointMsg with 
                        | EndpointMsg.AddServer server ->
                            match Map.tryFind server.Address model.Endpoints with 
                            | Some (reachable, _) ->
                                match reachable with 
                                | RemoteActorReachable.No ->
                                    log.Info (sprintf "[CLIENT] [RemoteActorManager] Mark remote server as reachable %O" server.Address)
                                    let newEndpoints = model.Endpoints.Add (server.Address, (RemoteActorReachable.Yes, RemoteActor<_>.Create(clusterSystem, server)))
                                    clusterSystem.EventStream.Publish(EndpointsUpdatedEvent newEndpoints)
                                    return! loop { model with Endpoints = newEndpoints }

                                | RemoteActorReachable.Yes -> ()
                            | None ->

                                log.Info (sprintf "[CLIENT] [RemoteActorManager] Added remote server %O" server.Address)
                            
                                let newEndpoints = model.Endpoints.Add (server.Address, (RemoteActorReachable.Yes, RemoteActor<_>.Create(clusterSystem, server)))
                                clusterSystem.EventStream.Publish(EndpointsUpdatedEvent newEndpoints)

                                return! loop { model with Endpoints = newEndpoints }

                        | EndpointMsg.RemoveServer addr ->
                            let newModel = 
                                seedNodes 
                                |> Seq.tryFind (fun seedNode -> 
                                    String.Compare(seedNode.TrimEnd('/'), addr.ToString().TrimEnd('/'), true) = 0
                                )
                                |> function
                                    | Some _ ->
                                        let newEndpoints = 
                                            model.Endpoints
                                            |> Map.map (fun addrKey (reachAble, actor)  ->
                                                if addrKey = addr then 
                                                    log.Info(sprintf "Cannot remove seed node %O, instead mark it as unreachable" addr)
                                                    clusterSystem.EventStream.Publish(NodeServerIsolatedEvent (actor.GetIdentity()))
                                                    
                                                    (RemoteActorReachable.No, actor)
                                                else (reachAble, actor)
                                            )

                                        clusterSystem.EventStream.Publish(EndpointsUpdatedEvent newEndpoints)

                                        { model with Endpoints = newEndpoints }

                                    | None ->
                                        log.Info (sprintf "[CLIENT] [RemoteActorManager] Remove remote server %O" addr)
                                        let newEndpoints = model.Endpoints.Remove addr

                                        let remoteActor = 
                                            let (_, actor) = model.Endpoints.[addr]
                                            actor

                                        clusterSystem.EventStream.Publish(ServerRemovedEvent (remoteActor.GetIdentity()))
                                        clusterSystem.EventStream.Publish(EndpointsUpdatedEvent newEndpoints)

                                        { model with Endpoints = newEndpoints }

                            return! loop newModel

                        | EndpointMsg.AddClient _ | EndpointMsg.RemoveClient _ ->
                            log.Error (sprintf "[CLIENT] [RemoteActorManager] Cannot accept AddClient msg in client side")
                            return Unhandled
                    
                    | _ -> return Unhandled
                }
                loop { Endpoints = Map.empty }
            ))
            |> retype

    [<RequireQualifiedAccess>]
    module private CancelableAsk =
        [<RequireQualifiedAccess>]
        type Response =
            | Unreachable of Address
            | Timeout
            | MemberRemoved of Address
            | Success of obj

        type AskingInfo =
            { RemoteActorAddress: Address
              Sender: IActorRef<Response>
              Guid: Guid
              Timer: Timer option }

        type Msg<'ServerMsg> = Msg of remoteServer: RemoteActor<'ServerMsg> * remoteActorAdddress: Address * 'ServerMsg * TimeSpan option

        type private Timeout = Timeout of AskingInfo

        let createAgent 
            seedNodes
            callbackActor 
            name 
            serverRoleName 
            (clusterSystem: ActorSystem): IActorRef<Msg<'ServerMsg>> =

            let actor = 
                let remoteActorManager : IActorRef<EndpointMsg> = 
                    RemoteActorManager.createAgent seedNodes clusterSystem serverRoleName

                spawn clusterSystem name (props (fun ctx ->

                    let log = ctx.Log.Value
                    let rec loop (askingInfos: list<AskingInfo>) = actor {
                        let! msg = ctx.Receive() : IO<obj>
                        let sender = ctx.Sender()
                        match msg with
                        | LifecycleEvent e ->
                            match e with
                            | PreStart ->
                                let b = clusterSystem.EventStream.Subscribe(untyped ctx.Self, typeof<ServerRemovedEvent>)
                                let b = clusterSystem.EventStream.Subscribe(untyped ctx.Self, typeof<NodeServerIsolatedEvent>)
                                log.Info (sprintf "Actor subscribed to Cluster status updates: %O" ctx.Self)
                            
                            | PostStop ->
                                let b = clusterSystem.EventStream.Unsubscribe(untyped ctx.Self)
                                log.Info (sprintf "Actor unsubscribed from Cluster status updates: %O" ctx.Self)

                            | _ -> return Unhandled


                        | ServerUnreachableEvent server ->

                            let expiredInfos, guaranteedInfos = 
                                askingInfos
                                |> List.partition(fun askingInfo -> askingInfo.RemoteActorAddress = server.Address)

                            for expiredInfo in expiredInfos do

                                match expiredInfo.Timer with 
                                | Some timer -> 
                                    timer.Stop()
                                    timer.Dispose()
                                | None -> ()

                                expiredInfo.Sender <! Response.MemberRemoved expiredInfo.RemoteActorAddress
                                    
                                log.Info (sprintf "[CLIENT] Remove ask tasks after member %O removed" expiredInfo.RemoteActorAddress)
                                    
                            return! loop guaranteedInfos

                        | :? 'CallbackMsg as callback ->
                            match callbackActor with 
                            | Some callbackActor ->
                                if sender.Path.Name = serverRoleName then 
                                    remoteActorManager <! EndpointMsg.AddServer { Address = sender.Path.Address; Role = serverRoleName }

                                callbackActor <! callback
                            | None -> log.Error (sprintf "Cannot find a callback actor to process %O" callback)


                        | :? EndpointMsg as endpointMsg ->
                            remoteActorManager <<! endpointMsg

                        | :? Timeout as timeout ->
                            log.Info (sprintf "[CLIENT] Ask task timeout: %O" timeout)
                            let (Timeout (askingInfo)) = timeout

                            match askingInfos |> List.tryFindIndex (fun askingInfo0 -> askingInfo0.Guid = askingInfo.Guid) with 
                            | Some index ->
                                let askingInfo = askingInfos.[index]

                                askingInfo.Sender <! Response.Timeout

                                log.Info (sprintf "[CLIENT] Remove ask task %O %O after timeout" askingInfo.RemoteActorAddress askingInfo.Guid)
                                return! loop (askingInfos.[0..index - 1] @ askingInfos.[index + 1..askingInfos.Length - 1])
                            | _ ->
                                log.Error "[CLIENT] Timeout, but the ask task has been already removed"


                        | :? Msg<'ServerMsg> as msg ->
                            let (Msg (remoteServer, remoteActorAddress, msg, timeSpan)) = msg
                            let timer =
                                match timeSpan with
                                | Some timeSpan ->
                                    let timer = new Timer(timeSpan.TotalMilliseconds)
                                    Some timer
                                | None -> None

                            let askingInfo =
                                { RemoteActorAddress = remoteActorAddress
                                  Sender = sender
                                  Guid = Guid.NewGuid()
                                  Timer = timer }

                            match askingInfo.Timer with 
                            | Some timer ->
                                timer.Start()
                                timer.Elapsed.Add(fun _ ->
                                    timer.Stop()
                                    ctx.Self <! box (Timeout askingInfo)
                                )
                            | None -> ()

                            remoteServer <! msg

                            log.Info (sprintf "[CLIENT] Add ask task %O %O" askingInfo.RemoteActorAddress askingInfo.Guid)

                            return! loop (askingInfo :: askingInfos)

                        | value -> 
                            match askingInfos |> List.tryFindIndexBack (fun askingInfo -> askingInfo.RemoteActorAddress = sender.Path.Address), sender.Path.Name = serverRoleName with 
                            | Some index, true ->
                                let askingInfo = askingInfos.[index]
                                match askingInfo.Timer with 
                                | Some timer -> 
                                    timer.Stop()
                                    timer.Dispose()
                                | None -> ()

                                log.Info (sprintf "[CLIENT] Receive msg %O from remote server %O" value sender.Path.Address)
                                remoteActorManager <! EndpointMsg.AddServer { Address = askingInfo.RemoteActorAddress; Role = serverRoleName }
                                askingInfo.Sender <! Response.Success value

                                return! loop (askingInfos.[0..index - 1] @ askingInfos.[index + 1..askingInfos.Length - 1])

                            | _ ->
                                log.Error (sprintf "[CLIENT] Unhandled message %O" msg)
                                return Unhandled

                    }
                    loop []
                ))

            retype actor

            

    [<RequireQualifiedAccess>]
    type RemoteJob<'ServerMsg> = 
        | Tell of 'ServerMsg
        | Ask of 'ServerMsg * timespan: TimeSpan option

    [<RequireQualifiedAccess>]
    module JobScheduler =

        type private Model =
            { JobCount: int
              Endpoints: Map<Address, RemoteActorReachable * RemoteActor<obj>>}

        let createAgent seedNodes clusterSystem name serverRoleName (callbackActor: IActorRef<'CallbackMsg> option) : IActorRef<RemoteJob<'ServerMsg>> = 

            let (cancelableAskAgent: IActorRef<CancelableAsk.Msg<'ServerMsg>>) = 
                CancelableAsk.createAgent seedNodes callbackActor name serverRoleName clusterSystem

            let actor : IActorRef<RemoteJob<'ServerMsg>> = 
                spawnAnonymous clusterSystem (props (fun ctx ->
                    let log = ctx.Log.Value
                    let cluster = Cluster.Get(clusterSystem)
                    let rec loop (model: Model)  = actor {
                        let! receivedMsg = ctx.Receive() : IO<obj>
                        match receivedMsg with 
                        | LifecycleEvent e ->
                            match e with
                            | PreStart ->
                                let b = clusterSystem.EventStream.Subscribe(untyped ctx.Self, typeof<EndpointsUpdatedEvent>)

                                log.Info (sprintf "Actor subscribed to Cluster status updates: %O" ctx.Self)
                            
                            | PostStop ->
                                let b = clusterSystem.EventStream.Unsubscribe(untyped ctx.Self)

                                log.Info (sprintf "Actor unsubscribed from Cluster status updates: %O" ctx.Self)

                            | _ -> return Unhandled

                        | :? EndpointsUpdatedEvent as endpointsUpdatedEvent ->
                            let (EndpointsUpdatedEvent endpoints) = endpointsUpdatedEvent
                            return! loop { model with Endpoints = endpoints }

                        | :? RemoteJob<'ServerMsg> as job ->
                            let endpoints = model.Endpoints

                            if endpoints.Count = 0 then 
                                match job with 
                                | RemoteJob.Tell _  ->
                                    log.Warning(sprintf "Service unavailable, try again later. %O" receivedMsg)

                                | RemoteJob.Ask _ ->
                                    log.Warning(sprintf "Service unavailable, try again later. %O" receivedMsg)
                                    let error = Result.Error (sprintf "Service unavailable, try again later. %O" receivedMsg)
                                    ctx.Sender() <! error

                                return Unhandled
                            else 
                                let reachableEndpoints, unReachableEndpoints =
                                    endpoints
                                    |> Map.partition(fun address (reachable, _) ->
                                        match reachable with 
                                        | RemoteActorReachable.Yes -> true
                                        | RemoteActorReachable.No -> false
                                    )

                                let reachable, remoteServer =
                                    match reachableEndpoints.Count, unReachableEndpoints.Count with 
                                    | reachableCount, unReachableCount when reachableCount > 0 ->
                                        let pair = 
                                            reachableEndpoints
                                            |> Seq.item (model.JobCount % reachableCount)

                                        pair.Value
                                    | reachableCount, unReachableCount when reachableCount = 0 && unReachableCount > 0 ->
                                        let pair = 
                                            unReachableEndpoints
                                            |> Seq.item (model.JobCount % unReachableCount)

                                        pair.Value

                                    | _ -> failwith "Invalid token"

                                let remoteServer = RemoteActor.retype remoteServer 

                                match job with 
                                | RemoteJob.Tell msg -> 
                                    remoteServer <! msg
                                    return! loop { model with JobCount = model.JobCount + 1}

                                | RemoteJob.Ask (msg, timeSpan) ->
                                    let result = 
                                        let addr = remoteServer.Address
                                        match reachable with 
                                        | RemoteActorReachable.Yes ->
                                            let msg = CancelableAsk.Msg (remoteServer, addr , msg, timeSpan)
                                            let task = cancelableAskAgent.Underlying.Ask(msg)
                                            task.Result

                                        | RemoteActorReachable.No ->
                                            let sencond5 = TimeSpan.FromSeconds(5.)
                                            let msg = CancelableAsk.Msg (remoteServer, addr , msg, Some sencond5)
                                            let task = cancelableAskAgent.Underlying.Ask(msg)
                                            task.Result


                                    let (result: CancelableAsk.Response) = unbox result

                                    match result with 
                                    | CancelableAsk.Response.Unreachable addr ->
                                        let result: Result<obj, string> = 
                                            Result.Error (sprintf "Please make sure remote seed node is reachable, And manully send a 5s timed task to reconnect to remote seed node")
                                        let sender = ctx.Sender()
                                        sender <! result
                                        return! loop { model with JobCount = model.JobCount + 1}

                                    | CancelableAsk.Response.Success result ->
                                        let result: Result<obj, string> = Result.Ok result
                                        ctx.Sender() <! result
                                        return! loop { model with JobCount = model.JobCount + 1}

                                    | CancelableAsk.Response.MemberRemoved memberIdentity ->
                                        let sender = (ctx.Sender() :> IInternalTypedActorRef).Underlying
                                        ctx.Self.Underlying.Tell(receivedMsg, sender)
                                        return! loop { model with JobCount = model.JobCount + 1}

                                    | CancelableAsk.Response.Timeout ->
                                        let result: Result<obj, string> = Result.Error (sprintf "Time exceed %A" timeSpan)
                                        ctx.Sender() <! result
                                        return! loop { model with JobCount = model.JobCount + 1}

                        | _ -> 
                            log.Error (sprintf "[CLIENT] unexcepted msg %O" receivedMsg)
                            return Unhandled
                    }
                    loop { JobCount = 0; Endpoints = Map.empty }
                ))
                |> retype

            actor


type Client<'CallbackMsg,'ServerMsg>(systemName, name, serverRoleName, remotePort, seedPort, callback: Actor<'CallbackMsg> -> Effect<'CallbackMsg>, setParams) =
    let clusterConfig: Config = Config.createClusterConfig [name] systemName remotePort seedPort setParams

    let clusterSystem = System.create systemName clusterConfig

    let log = clusterSystem.Log

    let callbackActor = spawnAnonymous clusterSystem (props callback)

    let jobSchedulerAgent = 
        let seedNodes = (clusterConfig.GetStringList("akka.cluster.seed-nodes"))

        Client.JobScheduler.createAgent seedNodes clusterSystem name serverRoleName (Some callbackActor)


    let retryCount = (clusterConfig.GetInt("akka.cluster.client-ask-retry-count"))

    let retryTimeInterval = clusterConfig.GetTimeSpan("akka.cluster.client-ask-retry-time-interval")


    member x.ClusterConfig = clusterConfig

    member x.ClusterSystem = clusterSystem

    member x.Log = log

    interface ICanTell<'ServerMsg> with
        member this.Ask(arg1: 'ServerMsg, ?arg2: TimeSpan): Async<'Response> = async {
            let rec retry count =
                let result: Result<obj, string> = 
                    jobSchedulerAgent <? (Client.RemoteJob.Ask (arg1, arg2))
                    |> Async.RunSynchronously
                match result with 
                | Result.Error error -> 
                    if count >= retryCount then 
                        failwithf "%s" error 
                    else 
                        Thread.Sleep(retryTimeInterval)
                        retry (count + 1)

                | Result.Ok ok -> 
                    match ok with 
                    | :? ErrorResponse as error ->
                        let (ErrorResponse error) = error 
                        log.Error error
                        failwith error
                    | _ -> unbox ok

            return retry 1
        }

        member this.Tell(arg1: 'ServerMsg, arg2: IActorRef): unit = 
            jobSchedulerAgent <! (Client.RemoteJob.Tell arg1)

        member this.Underlying: ICanTell = 
            jobSchedulerAgent.Underlying 

