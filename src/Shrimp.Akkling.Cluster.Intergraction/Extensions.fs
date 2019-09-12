namespace Shrimp.Akkling.Cluster.Intergraction

open Akkling
open Akka.Cluster.Tools.Singleton
open System.Reflection
open System
open System.IO

module Extensions =

    type LoggerKind =
        | NLog = 0
        | Default = 1



    type LocalConfigBuildingArgs =
        { ``akka.actor.serializers``: string list
          ``akka.actor.serialization-bindings``: string list
          ``akka.logLevel``: string
          ``akka.loggers``: Set<LoggerKind> }

    with 
        static member DefaultValue = 
            { ``akka.actor.serializers`` = ["hyperion = \"Akka.Serialization.HyperionSerializer, Akka.Serialization.Hyperion\""]
              ``akka.actor.serialization-bindings`` = ["\"System.Object\" = hyperion"]
              ``akka.logLevel`` = "DEBUG" 
              ``akka.loggers`` = Set.ofList [LoggerKind.Default] }

    type ClusterConfigBuildingArgs =
        { ``akka.actor.serializers``: string list
          ``akka.actor.serialization-bindings``: string list
          //``akka.remote.DotNetty-netty.tcp.public-hostname``: string
          ``akka.remote.dotNetty-netty.tcp.hostname``: string
          /// -----------------------------------------------Additional configurations
          ``akka.cluster.auto-down-unreachable-after``: string
          ``akka.cluster.clientToUnreachableServer-max-ask-time``: string
          ``akka.cluster.client-ask-retry-count``: int
          ``akka.cluster.client-ask-retry-time-interval``: string
          /// -----------------------------------------------
          ``akka.logLevel``: string
          ``akka.loggers``: Set<LoggerKind> }

    with 
        static member DefaultValue =
            { ``akka.actor.serializers`` = 
                ["hyperion = \"Akka.Serialization.HyperionSerializer, Akka.Serialization.Hyperion\""]
              ``akka.actor.serialization-bindings`` = ["\"System.Object\" = hyperion"]
              ``akka.remote.dotNetty-netty.tcp.hostname`` = "localhost"
              ``akka.cluster.auto-down-unreachable-after`` = "5s"
              ``akka.cluster.clientToUnreachableServer-max-ask-time`` = "5s"
              ``akka.cluster.client-ask-retry-count`` = 2
              ``akka.cluster.client-ask-retry-time-interval`` = "600ms"
              ``akka.logLevel`` = "DEBUG"
              ``akka.loggers`` = Set.ofList [LoggerKind.Default] }
        
        member x.ToLocalConfigBuildingArgs() =
            { ``akka.actor.serializers`` = x.``akka.actor.serializers`` 
              ``akka.actor.serialization-bindings`` = x.``akka.actor.serialization-bindings`` 
              ``akka.logLevel`` = x.``akka.logLevel`` 
              ``akka.loggers`` = x.``akka.loggers`` }


    [<RequireQualifiedAccess>]
    module Config = 
        [<RequireQualifiedAccess>]
        module private Hopac =
            let toTextInSquareBrackets (lists: string list) =
                lists
                |> List.map (sprintf "\"%s\"")
                |> String.concat ","

            let toTextInBrace (lists: string list) =
                lists
                |> String.concat "\n"



        let private possibleFolders = 
            [ "../Assets"(*UWP*) ] 

        let internal createLocalConfig (setParams: LocalConfigBuildingArgs -> LocalConfigBuildingArgs) =
            let args = setParams LocalConfigBuildingArgs.DefaultValue
            let config =
                sprintf 
                    """
                        akka.actor {
                            serializers {
                                %s
                            }
                            serialization-bindings {
                              %s
                            }
                            loglevel = %s
                            loggers=[%s]	
                        }
                    """  (Hopac.toTextInBrace args.``akka.actor.serializers``)
                         (Hopac.toTextInBrace args.``akka.actor.serialization-bindings``)
                          args.``akka.logLevel``
                          ( let loggers = Set.toList args.``akka.loggers``
                            loggers
                            |> List.map (function 
                                | LoggerKind.NLog -> "Akka.Logger.NLog.NLogLogger, Akka.Logger.NLog"
                                | LoggerKind.Default -> "Akka.Event.DefaultLogger"
                                | _ -> failwith "Invalid token"
                            )
                            |> Hopac.toTextInSquareBrackets
                          )
                |> Configuration.parse

            config.WithFallback(Akka.Configuration.ConfigurationFactory.Default())

        let internal createClusterConfig (roles: string list) systemName remotePort seedPort (setParams: ClusterConfigBuildingArgs -> ClusterConfigBuildingArgs) = 
            let args = setParams ClusterConfigBuildingArgs.DefaultValue
            let config = 
                let configText = 
                    sprintf 
                        """
                        akka {
                            actor {
                              provider = cluster
                              serializers {
                                 %s
                              }
                              serialization-bindings {
                                %s
                              }
                            }
                          remote {
                            dot-netty.tcp {
                              public-hostname = %s
                              hostname = %s
                              port = %d
                            }
                          }
                          cluster {
                            auto-down-unreachable-after = %s
                            clientToUnreachableServer-max-ask-time = %s
                            client-ask-retry-count = %d
                            client-ask-retry-time-interval = %s
                            seed-nodes = [ "akka.tcp://%s@%s:%d/" ]
                            roles = [%s]
                          }
                          persistence {
                            journal.plugin = "akka.persistence.journal.inmem"
                            snapshot-store.plugin = "akka.persistence.snapshot-store.local"
                          }
                        loglevel = %s
                        loggers=[%s]	
                        }
                        """ (Hopac.toTextInBrace args.``akka.actor.serializers``)
                            (Hopac.toTextInBrace args.``akka.actor.serialization-bindings``)
                            args.``akka.remote.dotNetty-netty.tcp.hostname``
                            args.``akka.remote.dotNetty-netty.tcp.hostname``
                            remotePort
                            args.``akka.cluster.auto-down-unreachable-after``
                            args.``akka.cluster.clientToUnreachableServer-max-ask-time``
                            args.``akka.cluster.client-ask-retry-count``
                            args.``akka.cluster.client-ask-retry-time-interval``
                            systemName
                            args.``akka.remote.dotNetty-netty.tcp.hostname``
                            seedPort 
                            (Hopac.toTextInSquareBrackets roles)
                            args.``akka.logLevel``
                            ( let loggers = Set.toList args.``akka.loggers``
                              loggers
                              |> List.map (function 
                                  | LoggerKind.NLog -> "Akka.Logger.NLog.NLogLogger, Akka.Logger.NLog"
                                  | LoggerKind.Default -> "Akka.Event.DefaultLogger"
                                  | _ -> failwith "Invalid token"
                              )
                              |> Hopac.toTextInSquareBrackets
                            )

                Configuration.parse configText
            config.WithFallback(ClusterSingletonManager.DefaultConfig())


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
