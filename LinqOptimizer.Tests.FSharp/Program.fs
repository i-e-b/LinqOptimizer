﻿// Learn more about F# at http://fsharp.net
// See the 'F# Tutorial' project for more help.

namespace LinqOptimizer.Tests

open LinqOptimizer.FSharp
open System.Linq
open System.Collections.Generic
open System.Diagnostics

module Program = 

    let time f = 
        let sw = Stopwatch()
        sw.Start()
        let r = f()
        sw.Stop()
        printfn "Result : %A\nElapsed : %A" r sw.Elapsed

    [<EntryPoint>]
    let main argv = 

//        let z =
//            Query.range(1,10)
//            |> Query.map(fun i -> i,i * i)
//            |> Query.map(fun ((a,b) as tt) -> a + snd tt)
//            |> Query.run

        let max = 10
        let x = 
            Query.range(1, max + 1)
            |> Query.map(fun i -> i, i + 1, i + 2)
            |> Query.map(fun y -> id y)
            |> Query.filter (fun (a,b,c) -> a + b + c = 3 * a + 3)
            |> Query.length
            |> Query.run
//
//        let x = 
//            Query.range(1, max + 1)
//            |> Query.map(fun i -> i, i + 1, i + 2)
//            //|> Query.map(fun (a,b,c) -> (b,c,a))
//            |> Query.filter (fun (a,b,c) -> a * a + b * b = c * c)
//            |> Query.map(fun (a,b,c) -> (b,c,a))
//            |> Query.length
//            |> Query.run

        0 // return an integer exit code

        //            |> Query.collect(fun a ->
//                Enumerable.Range(a, max + 1 - a)
//                |> Seq.collect(fun b ->
//                    Enumerable.Range(b, max + 1 - b)
//                    |> Seq.map (fun c -> a, b, c)))