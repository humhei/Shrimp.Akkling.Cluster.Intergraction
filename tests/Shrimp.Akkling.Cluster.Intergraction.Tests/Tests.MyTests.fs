module Tests.MyTests
open Shrimp.Akkling.Cluster.Intergraction
open Expecto
open Shrimp.FSharp.Plus
let pass() = Expect.isTrue true "passed"
let fail() = Expect.isTrue false "failed"
let MyTests =
  testList "MyTests" [
    testCase "MyTest" <| fun _ -> 
      let m = BinaryFile.serialize "Hello" (BinaryPath "test.bin")
      let p = BinaryFile.deserialize<string> m
      pass()
  ]