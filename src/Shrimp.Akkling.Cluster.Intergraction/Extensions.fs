namespace Shrimp.Akkling.Cluster.Intergraction

open Akkling
open Akka.Cluster.Tools.Singleton
open System.Reflection
open System
open System.IO


    module Configuration = 
        [<RequireQualifiedAccess>]
        module private Hopac =
            let toTextInSquareBrackets (lists: string list) =
                lists
                |> List.map (sprintf "\"%s\"")
                |> String.concat ","


        type IConfigurationNode =
            abstract member GetConfigurationText: unit -> string

        [<Struct>]          
        type ActorSerializer =
            | Hyperion 
            | NewtonSoftJsonSerializer 
            | ByteArraySerializer 

        type ActorSerializers = ActorSerializers of Set<ActorSerializer>
        with 
            interface IConfigurationNode with 
                member x.GetConfigurationText() =
                    sprintf "akka.actor.serializers.%s"
                        (
                            let (ActorSerializers actorSerializers) = x 
                            actorSerializers
                            |> Set.toList
                            |> List.map (function 
                                | ActorSerializer.Hyperion -> "hyperion = \"Akka.Serialization.HyperionSerializer, Akka.Serialization.Hyperion\""
                                | ActorSerializer.NewtonSoftJsonSerializer -> "newtonSoftJson = \"Akka.Serialization.NewtonSoftJsonSerializer\""
                                | ActorSerializer.ByteArraySerializer -> "byteArray = \"Akka.Serialization.ByteArraySerializer\""
                            )
                            |> String.concat "\n" )

        type ActorSerializationBindings = ActorSerializationBindings of List<Type * ActorSerializer>
        with 
            interface IConfigurationNode with 
                member x.GetConfigurationText() =
                    let (ActorSerializationBindings serializationBindings) = x 
                    serializationBindings
                    |> List.distinctBy(fun (tp, _) -> tp.FullName)
                    |> List.map (fun (tp, serializer) ->
                        sprintf "akka.actor.serialization-bindings.\"%s\" = %s" tp.FullName 
                            (match serializer with 
                                | ActorSerializer.ByteArraySerializer -> "byteArray"
                                | ActorSerializer.Hyperion -> "hyperion"
                                | ActorSerializer.NewtonSoftJsonSerializer -> "newtonSoftJson" ) 
                    )
                    |> String.concat "\n" 

        [<Struct>]
        type Logger =
            | NLog 
            | Default 

        type Loggers = Loggers of Set<Logger>
        with 
            interface IConfigurationNode with 
                member x.GetConfigurationText() =
                    sprintf "akka.loggers = [%s]"
                        (
                            let (Loggers loggers) = x 
                            loggers
                            |> Set.toList
                            |> List.map (function 
                                | Logger.NLog -> "Akka.Logger.NLog.NLogLogger, Akka.Logger.NLog"
                                | Logger.Default -> "Akka.Event.DefaultLogger"
                            )
                            |> Hopac.toTextInSquareBrackets )

        [<Struct>]
        type LoggerLevel =
            | DEBUG
            interface IConfigurationNode with 
                member x.GetConfigurationText() =
                    sprintf "akka.loglevel = %s" (
                        match x with 
                        | LoggerLevel.DEBUG -> "DEBUG"
                    )
             
        [<Struct>]
        type PersistenceJouralPlugin =
            | Inmen 
            | LiteDBFSharp of connectionString: string
        with 
            interface IConfigurationNode with 
                member x.GetConfigurationText() =
                    match x with 
                    | PersistenceJouralPlugin.Inmen -> """
akka.persistence.journal.plugin = "akka.persistence.journal.inmem"
                        """
                    | PersistenceJouralPlugin.LiteDBFSharp connectionString -> 
                        sprintf """
akka.persistence.journal.plugin = akka.persistence.journal.litedb.fsharp
akka.persistence.journal.litedb.fsharp {
    class = "Akka.Persistence.LiteDB.FSharp.LiteDBJournal, Akka.Persistence.LiteDB.FSharp"
    plugin-dispatcher = "akka.actor.default-dispatcher"
    connection-string = "%s"
} 
                        """ connectionString

        [<Struct>]
        type PersistenceSnapShotStorePlugin =
            | Local 
            | LiteDBFSharp of connectionString: string
        with 
            interface IConfigurationNode with 
                member x.GetConfigurationText() =
                    match x with 
                    | PersistenceSnapShotStorePlugin.Local -> """
snapshot-store.plugin = "akka.persistence.snapshot-store.local"
                        """
                    | PersistenceSnapShotStorePlugin.LiteDBFSharp connectionString -> 
                        sprintf
                            """
akka.persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.litedb.fsharp"
akka.persistence.snapshot-store.litedb.fsharp {
    class = "Akka.Persistence.LiteDB.FSharp.LiteDBSnapshotStore, Akka.Persistence.LiteDB.FSharp"
    plugin-dispatcher = "akka.actor.default-dispatcher"
    connection-string = "%s"
}
                            """ connectionString


        type LocalConfigBuildingArgs =
            { ``akka.actor.serializers``: ActorSerializers
              ``akka.actor.serialization-bindings``: ActorSerializationBindings
              ``akka.persistence.journal.plugin``: PersistenceJouralPlugin option
              ``akka.persistence.sanpshot-store.plugin``: PersistenceSnapShotStorePlugin option
              ``akka.logLevel``: LoggerLevel
              ``akka.loggers``: Loggers }

        with 
            static member DefaultValue = 
                { ``akka.actor.serializers`` = ActorSerializers (Set.ofList [ActorSerializer.Hyperion])
                  ``akka.actor.serialization-bindings`` = ActorSerializationBindings [typeof<obj>, ActorSerializer.Hyperion]
                  ``akka.persistence.journal.plugin`` = None
                  ``akka.persistence.sanpshot-store.plugin`` = None
                  ``akka.logLevel`` = LoggerLevel.DEBUG
                  ``akka.loggers`` = Loggers (Set.ofList [Logger.Default]) }

            interface IConfigurationNode with 
                member x.GetConfigurationText() =
                    [ yield (x.``akka.actor.serializers`` :> IConfigurationNode).GetConfigurationText() 
                      yield (x.``akka.actor.serialization-bindings`` :> IConfigurationNode).GetConfigurationText() 
                      if x.``akka.persistence.journal.plugin``.IsSome then yield (x.``akka.persistence.journal.plugin``.Value :> IConfigurationNode).GetConfigurationText() 
                      if x.``akka.persistence.sanpshot-store.plugin``.IsSome then yield (x.``akka.persistence.sanpshot-store.plugin``.Value :> IConfigurationNode).GetConfigurationText()
                      yield (x.``akka.logLevel`` :> IConfigurationNode).GetConfigurationText() 
                      yield (x.``akka.loggers`` :> IConfigurationNode).GetConfigurationText() ]
                    |> String.concat "\n"


        type ClusterConfigBuildingArgs =
            { ``akka.actor.serializers``: ActorSerializers
              ``akka.actor.serialization-bindings``: ActorSerializationBindings
              //``akka.remote.DotNetty-netty.tcp.public-hostname``: string
              ``akka.remote.dot-netty.tcp.hostname``: string
              /// -----------------------------------------------Additional configurations
              ``akka.cluster.auto-down-unreachable-after``: string
              ``akka.cluster.clientToUnreachableServer-max-ask-time``: string
              ``akka.cluster.client-ask-retry-count``: int
              ``akka.cluster.client-ask-retry-time-interval``: string
              /// -----------------------------------------------
              ``akka.persistence.journal.plugin``: PersistenceJouralPlugin
              ``akka.persistence.sanpshot-store.plugin``: PersistenceSnapShotStorePlugin
              ``akka.logLevel``: LoggerLevel
              ``akka.loggers``: Loggers
            }
        with 
            static member DefaultValue =
                { ``akka.actor.serializers`` =  ActorSerializers (Set.ofList [ActorSerializer.Hyperion])
                  ``akka.actor.serialization-bindings`` = ActorSerializationBindings [typeof<obj>, ActorSerializer.Hyperion]
                  ``akka.remote.dot-netty.tcp.hostname`` = "localhost"
                  ``akka.cluster.auto-down-unreachable-after`` = "5s"
                  ``akka.cluster.clientToUnreachableServer-max-ask-time`` = "5s"
                  ``akka.cluster.client-ask-retry-count`` = 2
                  ``akka.cluster.client-ask-retry-time-interval`` = "600ms"
                  ``akka.persistence.journal.plugin`` = PersistenceJouralPlugin.Inmen
                  ``akka.persistence.sanpshot-store.plugin`` = PersistenceSnapShotStorePlugin.Local
                  ``akka.logLevel`` = LoggerLevel.DEBUG
                  ``akka.loggers`` = Loggers (Set.ofList [Logger.Default]) }

            member x.ToLocalConfigBuildingArgs() =
                { ``akka.actor.serializers`` = x.``akka.actor.serializers`` 
                  ``akka.actor.serialization-bindings`` = x.``akka.actor.serialization-bindings`` 
                  ``akka.persistence.journal.plugin`` = Some x.``akka.persistence.journal.plugin``
                  ``akka.persistence.sanpshot-store.plugin`` = Some x.``akka.persistence.sanpshot-store.plugin``
                  ``akka.logLevel`` = x.``akka.logLevel`` 
                  ``akka.loggers`` = x.``akka.loggers`` }

            interface IConfigurationNode with 
                member x.GetConfigurationText() =
                    [ yield (x.``akka.actor.serializers`` :> IConfigurationNode).GetConfigurationText() 
                      yield (x.``akka.actor.serialization-bindings`` :> IConfigurationNode).GetConfigurationText() 
                      yield (x.``akka.persistence.journal.plugin`` :> IConfigurationNode).GetConfigurationText() 
                      yield (x.``akka.persistence.sanpshot-store.plugin`` :> IConfigurationNode).GetConfigurationText()
                      yield (x.``akka.logLevel`` :> IConfigurationNode).GetConfigurationText() 
                      yield (x.``akka.loggers`` :> IConfigurationNode).GetConfigurationText() 
                      yield sprintf "akka.remote.dot-netty.tcp.public-hostname = %s" x.``akka.remote.dot-netty.tcp.hostname``
                      yield sprintf "akka.remote.dot-netty.tcp.hostname = %s" x.``akka.remote.dot-netty.tcp.hostname``
                      yield sprintf "akka.cluster.auto-down-unreachable-after = %s" x.``akka.cluster.auto-down-unreachable-after``
                      yield sprintf "akka.cluster.clientToUnreachableServer-max-ask-time = %s" x.``akka.cluster.clientToUnreachableServer-max-ask-time``
                      yield sprintf "akka.cluster.client-ask-retry-count = %d" x.``akka.cluster.client-ask-retry-count``
                      yield sprintf "akka.cluster.client-ask-retry-time-interval = %s" x.``akka.cluster.client-ask-retry-time-interval``
                    ]
                    |> String.concat "\n"


        let private possibleFolders = 
            [ "../Assets"(*UWP*) ] 

        let internal createLocalConfig (setParams: LocalConfigBuildingArgs -> LocalConfigBuildingArgs) =
            let args = setParams LocalConfigBuildingArgs.DefaultValue
            
            let config =
                (args :> IConfigurationNode).GetConfigurationText()
                |> Configuration.parse

            config.WithFallback(Akka.Configuration.ConfigurationFactory.Default())

        let internal createClusterConfig (roles: string list) systemName remotePort seedPort (setParams: ClusterConfigBuildingArgs -> ClusterConfigBuildingArgs) = 
            let args = setParams ClusterConfigBuildingArgs.DefaultValue
            
            let config =
                let text1 = (args :> IConfigurationNode).GetConfigurationText()
                let text2 = 
                    [ yield sprintf "akka.remote.dot-netty.tcp.port = %d" remotePort 
                      yield sprintf """akka.cluster.seed-nodes = [ "akka.tcp://%s@%s:%d/" ]""" systemName args.``akka.remote.dot-netty.tcp.hostname`` seedPort 
                      yield sprintf "akka.cluster.roles = [%s]" (Hopac.toTextInSquareBrackets roles)
                    ] |> String.concat "\n"

                text1 + "\n" + text2
                |> Configuration.parse

            let s = ClusterSingletonManager.DefaultConfig()
            config.WithFallback(ClusterSingletonManager.DefaultConfig())

        [<RequireQualifiedAccess>]
        module Configuration = 
            /// application.conf should be copied to target folder
            let fallBackByApplicationConf config =
                let folder = System.IO.Path.GetDirectoryName(Assembly.GetEntryAssembly().Location)
                let folders = 
                    [ folder ] 
                    @ possibleFolders
                      |> List.map (fun m -> Path.Combine(folder, m))

                folders 
                |> List.map (fun folder -> Path.Combine(folder, "application.conf"))
                |> List.tryFind (fun file -> File.Exists(file))
                |> function
                    | Some file ->
                        let texts = File.ReadAllText(file, Text.Encoding.UTF8)
                        let applicationConfig = Configuration.parse(texts)
                        applicationConfig.WithFallback(config)
                    | None -> config
