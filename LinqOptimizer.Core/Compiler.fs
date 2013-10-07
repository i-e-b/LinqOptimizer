﻿namespace LinqOptimizer.Core
    
    open System
    open System.Collections
    open System.Collections.Generic
    open System.Linq
    open System.Linq.Expressions
    open System.Reflection

    module internal Compiler =
        let listTypeDef = typedefof<List<_>>

        let breakLabel () = labelTarget "break"
        let continueLabel () = labelTarget "continue"
        let lookup name (varExprs : ParameterExpression list) =
            varExprs |> List.find (fun varExpr -> varExpr.Name = name)  

        type QueryContext = { CurrentVarExpr : ParameterExpression; AccVarExpr : ParameterExpression; 
                                BreakLabel : LabelTarget; ContinueLabel : LabelTarget;
                                InitExprs : Expression list; AccExpr : Expression; CombinerExpr : Expression; ReturnExpr : Expression; 
                                VarExprs : ParameterExpression list; Exprs : Expression list }


        let toListContext (queryExpr : QueryExpr) =
                let listType = listTypeDef.MakeGenericType [| queryExpr.Type |]
                let finalVarExpr, accVarExpr  = var "___final___" queryExpr.Type, var "___acc___" listType
                let initExpr, accExpr = assign accVarExpr (``new`` listType), call (listType.GetMethod("Add")) accVarExpr [finalVarExpr]
                let context = { CurrentVarExpr = finalVarExpr; AccVarExpr = accVarExpr; BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                InitExprs = [initExpr]; AccExpr = accExpr; CombinerExpr = empty; ReturnExpr = accVarExpr; 
                                VarExprs = [finalVarExpr; accVarExpr]; Exprs = [] }
                context
            
        let rec compileToSeqPipeline (queryExpr : QueryExpr) (context : QueryContext) : Expression =
            match queryExpr with
            | Source (ExprType (Array (_, 1)) as expr, t) ->
                    let indexVarExpr = var "___index___" typeof<int>
                    let arrayVarExpr = var "___array___" expr.Type
                    let arrayAssignExpr = assign arrayVarExpr expr
                    let indexAssignExpr = assign indexVarExpr (constant -1) 
                    let lengthExpr = arrayLength arrayVarExpr 
                    let getItemExpr = arrayIndex arrayVarExpr indexVarExpr
                    let exprs' = assign context.CurrentVarExpr getItemExpr :: context.Exprs
                    let checkBoundExpr = equal indexVarExpr lengthExpr 
                    let brachExpr = ``ifThenElse`` checkBoundExpr (``break`` context.BreakLabel) (block [] exprs') 
                    let loopExpr = loop (block [] [addAssign indexVarExpr (constant 1); brachExpr; context.AccExpr]) context.BreakLabel context.ContinueLabel 
                    block (arrayVarExpr :: indexVarExpr :: context.VarExprs) [block [] context.InitExprs; arrayAssignExpr; indexAssignExpr; loopExpr; context.ReturnExpr] 
            | Source (ExprType (Named (TypeCheck listTypeDef _, [|_|])) as expr, t) ->
                    let indexVarExpr = var "___index___" typeof<int>
                    let listVarExpr = var "___list___" expr.Type
                    let listAssignExpr = assign listVarExpr expr
                    let indexAssignExpr = assign indexVarExpr (constant -1) 
                    let lengthExpr = call (expr.Type.GetMethod("get_Count")) listVarExpr []
                    let getItemExpr = call (expr.Type.GetMethod("get_Item")) listVarExpr [indexVarExpr]
                    let exprs' = assign context.CurrentVarExpr getItemExpr :: context.Exprs
                    let checkBoundExpr = equal indexVarExpr lengthExpr 
                    let brachExpr = ``ifThenElse`` checkBoundExpr (``break`` context.BreakLabel) (block [] exprs') 
                    let loopExpr = loop (block [] [addAssign indexVarExpr (constant 1); brachExpr; context.AccExpr]) context.BreakLabel context.ContinueLabel 
                    block (listVarExpr :: indexVarExpr :: context.VarExprs) [block [] context.InitExprs; listAssignExpr; indexAssignExpr; loopExpr; context.ReturnExpr] 
            | Source (expr, t) -> // general case for IEnumerable
                    let enumerableType = typedefof<IEnumerable<_>>.MakeGenericType [| t |]
                    let enumeratorType = typedefof<IEnumerator<_>>.MakeGenericType [| t |]
                    let disposableVarExpr = var "___disposable___" typeof<IDisposable>
                    let enumeratorVarExpr = var "___enumerator___" enumeratorType
                    let enumeratorAssignExpr = assign enumeratorVarExpr (call (enumerableType.GetMethod("GetEnumerator")) expr [])
                    let disposableAssignExpr = assign disposableVarExpr enumeratorVarExpr 
                    let getItemExpr = call (enumeratorType.GetMethod("get_Current")) enumeratorVarExpr []
                    let exprs' = assign context.CurrentVarExpr getItemExpr :: context.Exprs
                    let checkBoundExpr = equal (call (typeof<IEnumerator>.GetMethod("MoveNext")) enumeratorVarExpr []) (constant false)
                    let brachExpr = ``ifThenElse`` checkBoundExpr (``break`` context.BreakLabel) (block [] exprs') 
                    let loopExpr = tryfinally (loop (block [] [brachExpr; context.AccExpr]) context.BreakLabel context.ContinueLabel) (call (typeof<IDisposable>.GetMethod("Dispose")) disposableVarExpr [])
                    block (enumeratorVarExpr :: disposableVarExpr :: context.VarExprs) [block [] context.InitExprs; enumeratorAssignExpr; disposableAssignExpr; loopExpr; context.ReturnExpr] 
            | ZipWith((ExprType (Array (_, 1)) as expr1, t1),(ExprType (Array (_, 1)) as expr2,t2), (Lambda ([param1Expr; param2Expr], bodyExpr) as func)) ->
                    let indexVarExpr = var "___index___" typeof<int>
                    let indexAssignExpr = assign indexVarExpr (constant -1) 
                         
                    let array1VarExpr = var "___array1___" expr1.Type
                    let array1AssignExpr = assign array1VarExpr expr1
                    let length1Expr = arrayLength array1VarExpr 
                    let getItem1Expr = arrayIndex array1VarExpr indexVarExpr

                    let array2VarExpr = var "___array2___" expr2.Type
                    let array2AssignExpr = assign array2VarExpr expr2
                    let length2Expr = arrayLength array2VarExpr 
                    let getItem2Expr = arrayIndex array2VarExpr indexVarExpr

                    let param1AssignExpr = assign param1Expr getItem1Expr
                    let param2AssignExpr = assign param2Expr getItem2Expr
                    let getItemExpr = bodyExpr
                        
                    let exprs' = param1AssignExpr ::  param2AssignExpr :: assign context.CurrentVarExpr getItemExpr :: context.Exprs
                    let checkBound1Expr = equal indexVarExpr length1Expr 
                    let checkBound2Expr = equal indexVarExpr length2Expr
                    let checkBoundExpr = Expression.Or(checkBound1Expr, checkBound2Expr)
                    let branchExpr = ``ifThenElse`` checkBoundExpr (``break`` context.BreakLabel) (block [] exprs')

                    let loopExpr = loop (block [] [addAssign indexVarExpr (constant 1); branchExpr; context.AccExpr]) context.BreakLabel context.ContinueLabel
                    let vars =   param1Expr :: param2Expr ::array1VarExpr :: array2VarExpr :: indexVarExpr :: context.VarExprs
                    block vars
                        [block [] context.InitExprs; array1AssignExpr; array2AssignExpr; indexAssignExpr; loopExpr; context.ReturnExpr]
            | ZipWith((ExprType (Named (TypeCheck listTypeDef _, [|_|])) as expr1, t1),(ExprType (Named (TypeCheck listTypeDef _, [|_|])) as expr2,t2), (Lambda ([param1Expr; param2Expr], bodyExpr) as func)) ->
                    let indexVarExpr = var "___index___" typeof<int>
                    let indexAssignExpr = assign indexVarExpr (constant -1) 
                        
                    let list1VarExpr = var "___list1___" expr1.Type
                    let list1AssignExpr = assign list1VarExpr expr1
                    let length1Expr = call (expr1.Type.GetMethod("get_Count")) list1VarExpr []
                    let getItem1Expr = call (expr1.Type.GetMethod("get_Item")) list1VarExpr [indexVarExpr]

                    let list2VarExpr = var "___list2___" expr2.Type
                    let list2AssignExpr = assign list2VarExpr expr2
                    let length2Expr = call (expr2.Type.GetMethod("get_Count")) list2VarExpr []
                    let getItem2Expr = call (expr2.Type.GetMethod("get_Item")) list2VarExpr [indexVarExpr]
                                                
                    let param1AssignExpr = assign param1Expr getItem1Expr
                    let param2AssignExpr = assign param2Expr getItem2Expr
                    let getItemExpr = bodyExpr
                        
                    let exprs' = param1AssignExpr ::  param2AssignExpr :: assign context.CurrentVarExpr getItemExpr :: context.Exprs
                    let checkBound1Expr = equal indexVarExpr length1Expr 
                    let checkBound2Expr = equal indexVarExpr length2Expr
                    let checkBoundExpr = Expression.Or(checkBound1Expr, checkBound2Expr)
                    let branchExpr = ``ifThenElse`` checkBoundExpr (``break`` context.BreakLabel) (block [] exprs')

                    let loopExpr = loop (block [] [addAssign indexVarExpr (constant 1); branchExpr; context.AccExpr]) context.BreakLabel context.ContinueLabel
                    let vars = param1Expr :: param2Expr ::list1VarExpr :: list2VarExpr :: indexVarExpr :: context.VarExprs
                    block vars
                        [block [] context.InitExprs; list1AssignExpr; list2AssignExpr; indexAssignExpr; loopExpr; context.ReturnExpr]
            | ZipWith((expr1, t1),(expr2,t2), (Lambda ([param1Expr; param2Expr], bodyExpr) as func) ) ->
                    let enumerable1Type = typedefof<IEnumerable<_>>.MakeGenericType [| t1 |]
                    let enumerator1Type = typedefof<IEnumerator<_>>.MakeGenericType [| t1 |]
                    let disposable1VarExpr = var "___disposable1___" typeof<IDisposable>
                    let enumerator1VarExpr = var "___enumerator1___" enumerator1Type
                    let enumerator1AssignExpr = assign enumerator1VarExpr (call (enumerable1Type.GetMethod("GetEnumerator")) expr1 [])
                    let disposable1AssignExpr = assign disposable1VarExpr enumerator1VarExpr 
                    let getItem1Expr = call (enumerator1Type.GetMethod("get_Current")) enumerator1VarExpr []

                    let enumerable2Type = typedefof<IEnumerable<_>>.MakeGenericType [| t2 |]
                    let enumerator2Type = typedefof<IEnumerator<_>>.MakeGenericType [| t2 |]
                    let disposable2VarExpr = var "___disposable2___" typeof<IDisposable>
                    let enumerator2VarExpr = var "___enumerator2___" enumerator2Type
                    let enumerator2AssignExpr = assign enumerator2VarExpr (call (enumerable2Type.GetMethod("GetEnumerator")) expr2 [])
                    let disposable2AssignExpr = assign disposable2VarExpr enumerator2VarExpr 
                    let getItem2Expr = call (enumerator2Type.GetMethod("get_Current")) enumerator2VarExpr []

                    let param1AssignExpr = assign param1Expr getItem1Expr
                    let param2AssignExpr = assign param2Expr getItem2Expr
                    let getItemExpr = bodyExpr

                    let exprs' = param1AssignExpr ::  param2AssignExpr :: assign context.CurrentVarExpr getItemExpr :: context.Exprs

                    let checkBound1Expr = equal (call (typeof<IEnumerator>.GetMethod("MoveNext")) enumerator1VarExpr []) (constant false)
                    let checkBound2Expr = equal (call (typeof<IEnumerator>.GetMethod("MoveNext")) enumerator2VarExpr []) (constant false)
                    let checkBoundExpr = Expression.Or(checkBound1Expr, checkBound2Expr)
                    let branchExpr = ``ifThenElse`` checkBoundExpr (``break`` context.BreakLabel) (block [] exprs')
                    let disposeCallExpr = block [] [ (call (typeof<IDisposable>.GetMethod("Dispose")) disposable1VarExpr []);
                                                        (call (typeof<IDisposable>.GetMethod("Dispose")) disposable2VarExpr []) ]
                    let loopExpr = tryfinally (loop (block [] [branchExpr; context.AccExpr]) context.BreakLabel context.ContinueLabel) disposeCallExpr
                    let vars =  
                        param1Expr :: param2Expr :: enumerator1VarExpr :: disposable1VarExpr :: 
                        enumerator2VarExpr :: disposable2VarExpr :: context.VarExprs
                    block vars 
                        [block [] context.InitExprs; enumerator1AssignExpr; disposable1AssignExpr; enumerator2AssignExpr; disposable2AssignExpr; loopExpr; context.ReturnExpr]
            | RangeGenerator(start, count) ->
                    let startExpr = constant (start-1)
                    let endExpr = constant (start + count)
                    let currVarExpr = var "___curr___" typeof<int>
                    let currVarInitExpr = assign currVarExpr startExpr
                    let checkExpr = equal currVarExpr endExpr
                    let incCurrExpr = addAssign currVarExpr (constant 1)
                    let exprs' = assign context.CurrentVarExpr currVarExpr :: context.Exprs
                    let branchExpr = ``ifThenElse`` checkExpr (``break`` context.BreakLabel) (block [] exprs')
                    let loopExpr = loop (block [] [incCurrExpr; branchExpr ; context.AccExpr]) context.BreakLabel context.ContinueLabel
                    block (currVarExpr :: context.VarExprs) [block [] context.InitExprs; currVarInitExpr; loopExpr; context.ReturnExpr ]
            | RepeatGenerator(element, t, count) ->
                    let endExpr = constant count
                    let indexVarExpr = var "___index___" typeof<int>
                    let indexVarInitExpr = assign indexVarExpr (constant count)
                    let elemVarExpr = var "___elem___" t
                    let elemVarInitExpr = assign elemVarExpr (cast (constant element) t)
                    let checkExpr = lessThan indexVarExpr (constant 0)
                    let incCurrExpr = subAssign indexVarExpr (constant 1)
                    let exprs' = assign context.CurrentVarExpr elemVarExpr :: context.Exprs
                    let branchExpr = ``ifThenElse`` checkExpr (``break`` context.BreakLabel) (block [] exprs')
                    let loopExpr = loop (block [] [incCurrExpr; branchExpr; context.AccExpr]) context.BreakLabel context.ContinueLabel
                    block (indexVarExpr :: elemVarExpr :: context.VarExprs) [block [] context.InitExprs; elemVarInitExpr; indexVarInitExpr; loopExpr; context.ReturnExpr ]
            | Transform (Lambda ([paramExpr], bodyExpr), queryExpr', _) ->
                let exprs' = assign context.CurrentVarExpr bodyExpr :: context.Exprs
                compileToSeqPipeline queryExpr' { context with CurrentVarExpr = paramExpr; VarExprs = paramExpr :: context.VarExprs; Exprs = exprs' }
            | TransformIndexed (Lambda ([paramExpr; indexExpr], bodyExpr), queryExpr', _) ->
                let exprs' = addAssign indexExpr (constant 1) :: assign context.CurrentVarExpr bodyExpr :: context.Exprs
                compileToSeqPipeline queryExpr' { context with CurrentVarExpr = paramExpr; InitExprs = assign indexExpr (constant -1) :: context.InitExprs; VarExprs = paramExpr :: indexExpr :: context.VarExprs; Exprs = exprs' }
            | Filter (Lambda ([paramExpr], bodyExpr), queryExpr', _) ->
                let exprs' = ``ifThenElse`` bodyExpr empty (``continue`` context.ContinueLabel) :: assign context.CurrentVarExpr paramExpr :: context.Exprs
                compileToSeqPipeline queryExpr' { context with CurrentVarExpr = paramExpr; VarExprs = paramExpr :: context.VarExprs; Exprs = exprs' }
            | FilterIndexed (Lambda ([paramExpr; indexExpr], bodyExpr), queryExpr', _) ->
                let exprs' = addAssign indexExpr (constant 1) :: ``ifThenElse`` bodyExpr empty (``continue`` context.ContinueLabel) :: assign context.CurrentVarExpr paramExpr :: context.Exprs
                compileToSeqPipeline queryExpr' { context with CurrentVarExpr = paramExpr; InitExprs = assign indexExpr (constant -1) :: context.InitExprs; VarExprs = indexExpr :: paramExpr :: context.VarExprs; Exprs = exprs' }
            | Take (countExpr, queryExpr', _) ->
                let countVarExpr = var "___takeCount___" typeof<int> //special "local" variable for Take
                let exprs' = addAssign countVarExpr (constant 1) :: ``ifThenElse`` (greaterThan countVarExpr countExpr) (``break`` context.BreakLabel) empty :: context.Exprs
                compileToSeqPipeline queryExpr' { context with InitExprs = assign countVarExpr (constant 0) :: context.InitExprs ; VarExprs = countVarExpr :: context.VarExprs; Exprs = exprs' }
            | Skip (countExpr, queryExpr', _) ->
                let countVarExpr = var "___skipCount___" typeof<int> //special "local" variable for Skip
                let exprs' = addAssign countVarExpr (constant 1) :: ``ifThenElse`` (lessThanOrEqual countVarExpr countExpr) (``continue`` context.ContinueLabel) empty :: context.Exprs
                compileToSeqPipeline queryExpr' { context with InitExprs = assign countVarExpr (constant 0) :: context.InitExprs ; VarExprs = countVarExpr :: context.VarExprs; Exprs = exprs' }
            | NestedQuery ((paramExpr, nestedQueryExpr), queryExpr', t) ->
                let context' = { CurrentVarExpr = context.CurrentVarExpr; AccVarExpr = context.AccVarExpr; BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                    InitExprs = [empty]; AccExpr = context.AccExpr; CombinerExpr = empty; ReturnExpr = empty; 
                                    VarExprs = []; Exprs = context.Exprs }

                let expr = compileToSeqPipeline nestedQueryExpr context'
                compileToSeqPipeline queryExpr' { context with CurrentVarExpr = paramExpr; AccExpr = empty; VarExprs = paramExpr :: context.VarExprs; Exprs = [expr] }
            | NestedQueryTransform ((paramExpr, nestedQueryExpr), Lambda ([valueExpr; colExpr], bodyExpr), queryExpr', t) ->
                let context' = { CurrentVarExpr = valueExpr; AccVarExpr = context.AccVarExpr; BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                    InitExprs = [empty]; AccExpr = context.AccExpr; CombinerExpr = empty; ReturnExpr = empty; 
                                    VarExprs = []; Exprs = assign colExpr paramExpr :: assign context.CurrentVarExpr bodyExpr :: context.Exprs }

                let expr = compileToSeqPipeline nestedQueryExpr context'
                compileToSeqPipeline queryExpr' { context with CurrentVarExpr = paramExpr; AccExpr = empty; VarExprs = paramExpr :: valueExpr :: colExpr :: context.VarExprs; Exprs = [expr] }
            | GroupBy (Lambda ([paramExpr], bodyExpr) as lambdaExpr, queryExpr', _) ->
                let listType = listTypeDef.MakeGenericType [| queryExpr'.Type |]
                let finalVarExpr, accVarExpr  = var "___final___" queryExpr'.Type, var "___acc___" listType
                let initExpr, accExpr = assign accVarExpr (``new`` listType), call (listType.GetMethod("Add")) accVarExpr [finalVarExpr]
                let context' = { CurrentVarExpr = finalVarExpr; AccVarExpr = accVarExpr; BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                    InitExprs = [initExpr]; AccExpr = accExpr; CombinerExpr = empty; ReturnExpr = empty; 
                                    VarExprs = [finalVarExpr]; Exprs = [] }
                let expr = compileToSeqPipeline queryExpr' context'
                let groupByMethodInfo = typeof<Enumerable>.GetMethods()
                                            |> Array.find (fun methodInfo -> 
                                                            match methodInfo with
                                                            | MethodName "GroupBy" [|_; _|] -> true
                                                            | _ -> false) // TODO: reflection type checks
                                            |> (fun methodInfo -> methodInfo.MakeGenericMethod [|paramExpr.Type; bodyExpr.Type|])
                let groupingType = typedefof<IGrouping<_, _>>.MakeGenericType [|paramExpr.Type; bodyExpr.Type|]
                let groupByCallExpr = call groupByMethodInfo null [accVarExpr; lambdaExpr]
                let expr' = compileToSeqPipeline (Source (groupByCallExpr, groupingType)) context
                block [accVarExpr] [expr; expr']
            | OrderBy (Lambda ([paramExpr], bodyExpr) as lambdaExpr, order, queryExpr', t) ->
                let listType = listTypeDef.MakeGenericType [| queryExpr'.Type |]
                let finalVarExpr, accVarExpr  = var "___final___" queryExpr'.Type, var "___acc___" listType
                let initExpr, accExpr = assign accVarExpr (``new`` listType), call (listType.GetMethod("Add")) accVarExpr [finalVarExpr]
                let context' = { CurrentVarExpr = finalVarExpr; AccVarExpr = accVarExpr; BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                    InitExprs = [initExpr]; AccExpr = accExpr; CombinerExpr = empty; ReturnExpr = empty; 
                                    VarExprs = [finalVarExpr]; Exprs = [] }
                let expr = compileToSeqPipeline queryExpr' context'
                let methodName = match order with Ascending -> "OrderBy" | Descending -> "OrderByDescending"
                let orderByMethodInfo = typeof<Enumerable>.GetMethods()
                                            |> Array.find (fun methodInfo -> 
                                                            match methodInfo with
                                                            | MethodName methodName [|_; _|] -> true
                                                            | _ -> false) // TODO: reflection type checks
                                            |> (fun methodInfo -> methodInfo.MakeGenericMethod [|paramExpr.Type; bodyExpr.Type|])
                let orderByCallExpr = call orderByMethodInfo null [accVarExpr; lambdaExpr]
                let expr' = compileToSeqPipeline (Source (orderByCallExpr, t)) context
                block [accVarExpr] [expr; expr']
            | _ -> failwithf "Invalid state %A" queryExpr 

        let rec compileToSequential (queryExpr : QueryExpr) : Expression = 
            match queryExpr with
            | Sum (queryExpr', t) ->
                let finalVarExpr = var "___final___" t
                let accVarExpr = var "___acc___" t
                let initExpr = assign accVarExpr (``default`` t)
                let accExpr = addAssign accVarExpr finalVarExpr
                let context = { CurrentVarExpr = finalVarExpr; AccVarExpr = accVarExpr; BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                InitExprs = [initExpr]; AccExpr = accExpr; CombinerExpr = empty; ReturnExpr = accVarExpr; 
                                VarExprs = [finalVarExpr; accVarExpr]; Exprs = [] }
                let expr = compileToSeqPipeline queryExpr' context
                expr 
            | Count (queryExpr', t) ->
                let accVarExpr = var "___cnt___" typeof<int>
                let initExpr = assign accVarExpr (constant 0)
                let accExpr = addAssign accVarExpr (constant 1)
                let tmpVarExpr = var "___tmp___" t
                let context = { CurrentVarExpr = tmpVarExpr; AccVarExpr = accVarExpr; BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                InitExprs = [initExpr]; AccExpr = accExpr; CombinerExpr = empty; ReturnExpr = accVarExpr; 
                                VarExprs = [tmpVarExpr; accVarExpr]; Exprs = [] }
                let expr = compileToSeqPipeline queryExpr' context
                expr 
            | Aggregate ((seed, t), Lambda ([accVarExpr; varExpr], bodyExpr), queryExpr') ->
                let initExpr = assign accVarExpr (constant seed)
                let accExpr = assign accVarExpr bodyExpr
                let context = { CurrentVarExpr = varExpr; AccVarExpr = accVarExpr; BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                InitExprs = [initExpr]; AccExpr = accExpr; CombinerExpr = empty; ReturnExpr = accVarExpr; 
                                VarExprs = [varExpr; accVarExpr]; Exprs = [] }
                let expr = compileToSeqPipeline queryExpr' context
                expr
            | ForEach (Lambda ([paramExpr], bodyExpr), queryExpr') ->
                let context = { CurrentVarExpr = paramExpr; AccVarExpr = var "___empty___" typeof<unit>; BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                InitExprs = [assign paramExpr (``default`` paramExpr.Type)]; 
                                AccExpr = bodyExpr; CombinerExpr = empty; ReturnExpr = empty; 
                                VarExprs = [paramExpr]; Exprs = [] }
                let expr = compileToSeqPipeline queryExpr' context
                expr
            | ToArray queryExpr' ->
                let listType = listTypeDef.MakeGenericType [| queryExpr'.Type |]
                let expr = compileToSequential (ToList queryExpr')
                let expr' = call (listType.GetMethod "ToArray") expr []
                expr' 
            | ToList queryExpr' ->
                let context = toListContext queryExpr'
                let expr = compileToSeqPipeline queryExpr' context
                expr
            | queryExpr' ->
                let context = toListContext queryExpr'
                let expr = compileToSeqPipeline queryExpr context
                expr

        let compileToParallel (queryExpr : QueryExpr) : Expression =
            let rec compile queryExpr context =
                match queryExpr with
                | Source (expr, t) -> 
                    let aggregateMethodInfo = typeof<ParallelEnumerable>.GetMethods()
                                                |> Array.find (fun methodInfo -> 
                                                                match methodInfo with
                                                                | MethodName "Aggregate" [|_; ParameterName "seedFactory" _; _; _; _|] -> true
                                                                | _ -> false) // TODO: reflection type checks
                                                |> (fun methodInfo -> methodInfo.MakeGenericMethod [|context.CurrentVarExpr.Type; context.AccVarExpr.Type; context.AccVarExpr.Type|])
                    let accExpr = lambda [|context.AccVarExpr; context.CurrentVarExpr|] 
                                            (block (context.VarExprs |> List.filter (fun var -> not (var = context.CurrentVarExpr))) 
                                            (context.Exprs @ [context.AccExpr; label context.BreakLabel; context.AccVarExpr]))
                    call aggregateMethodInfo null [expr; List.head context.InitExprs; accExpr; context.CombinerExpr; context.ReturnExpr]
                | Transform (Lambda ([paramExpr], bodyExpr), queryExpr', _) ->
                    let exprs' = assign context.CurrentVarExpr bodyExpr :: context.Exprs
                    compile queryExpr' { context with CurrentVarExpr = paramExpr; VarExprs = paramExpr :: context.VarExprs; Exprs = exprs' }
                | Filter (Lambda ([paramExpr], bodyExpr), queryExpr', _) ->
                    let exprs' = ``ifThen`` (notExpr bodyExpr) (goto context.BreakLabel) :: assign context.CurrentVarExpr paramExpr :: context.Exprs
                    compile queryExpr' { context with CurrentVarExpr = paramExpr; VarExprs = paramExpr :: context.VarExprs; Exprs = exprs' }
                | NestedQuery ((paramExpr, nestedQueryExpr), queryExpr', t) ->
                    let context' = { CurrentVarExpr = context.CurrentVarExpr; AccVarExpr = context.AccVarExpr; BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                        InitExprs = [empty]; AccExpr = context.AccExpr; CombinerExpr = empty; ReturnExpr = empty; 
                                        VarExprs = []; Exprs = context.Exprs }

                    let expr = compileToSeqPipeline nestedQueryExpr context'
                    compile queryExpr' { context with CurrentVarExpr = paramExpr; AccExpr = empty; VarExprs = paramExpr :: context.VarExprs; Exprs = [expr] }
                | _ -> failwithf "Invalid state %A" queryExpr 
            match queryExpr with
            | Sum (queryExpr', t) ->
                let finalVarExpr = var "___final___" t
                let accVarExpr = var "___acc___" t
                let initExpr = lambda [||] (``default`` t)
                let accExpr = addAssign accVarExpr finalVarExpr
                let leftVarExpr, rightVarExpr = var "___left___" t, var "___right___" t
                let combinerExpr = lambda [|leftVarExpr; rightVarExpr|] (block [] [addAssign leftVarExpr rightVarExpr; leftVarExpr])
                let returnExpr = lambda [|accVarExpr|] accVarExpr
                let context = { CurrentVarExpr = finalVarExpr; AccVarExpr = accVarExpr; BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                InitExprs = [initExpr]; AccExpr = accExpr; CombinerExpr = combinerExpr; ReturnExpr = returnExpr; 
                                VarExprs = [finalVarExpr]; Exprs = [] }
                let expr = compile queryExpr' context
                expr 
            | queryExpr' ->
                let listType = listTypeDef.MakeGenericType [| queryExpr'.Type |]
                let finalVarExpr, accVarExpr  = var "___final___" queryExpr'.Type, var "___acc___" listType
                let initExpr, accExpr = lambda [||] (``new`` listType), block [] [call (listType.GetMethod("Add")) accVarExpr [finalVarExpr]; accVarExpr]
                let leftVarExpr, rightVarExpr = var "___left___" listType, var "___right___" listType
                let combinerExpr = lambda [|leftVarExpr; rightVarExpr|] (block [] [call (listType.GetMethod("AddRange")) leftVarExpr [rightVarExpr]; leftVarExpr])
                let returnExpr = lambda [|accVarExpr|] accVarExpr
                let context = { CurrentVarExpr = finalVarExpr; AccVarExpr = accVarExpr; BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                    InitExprs = [initExpr]; AccExpr = accExpr; CombinerExpr = combinerExpr; ReturnExpr = returnExpr; 
                                    VarExprs = [finalVarExpr]; Exprs = [] }
                let expr = compile queryExpr context
                expr

        let rec toQueryExpr (expr : Expression) : QueryExpr =
            // TODO: expr type checks
            match expr with
            | MethodCall (_, MethodName "Select" _, [expr'; Lambda ([_], bodyExpr) as f']) -> 
                Transform (f' :?> LambdaExpression, toQueryExpr expr', bodyExpr.Type)
            | MethodCall (_, MethodName "Select" _, [expr'; Lambda ([_; _], bodyExpr) as f']) -> 
                TransformIndexed (f' :?> LambdaExpression, toQueryExpr expr', bodyExpr.Type)
            | MethodCall (_, MethodName "Where" _, [expr'; Lambda ([paramExpr], _) as f']) -> 
                Filter (f' :?> LambdaExpression, toQueryExpr expr', paramExpr.Type)
            | MethodCall (_, MethodName "Where" _, [expr'; Lambda ([paramExpr; indexExpr], _) as f']) -> 
                FilterIndexed (f' :?> LambdaExpression, toQueryExpr expr', paramExpr.Type)
            | MethodCall (_, MethodName "Take" _, [expr'; countExpr]) -> 
                let queryExpr = toQueryExpr expr'
                Take (countExpr, queryExpr, queryExpr.Type)
            | MethodCall (_, MethodName "Skip" _, [expr'; countExpr]) -> 
                let queryExpr = toQueryExpr expr'
                Skip (countExpr, queryExpr, queryExpr.Type)
            | MethodCall (_, (MethodName "SelectMany" [|_; _|] as m), [expr'; Lambda ([paramExpr], bodyExpr)]) -> 
                NestedQuery ((paramExpr, toQueryExpr bodyExpr), toQueryExpr expr', m.ReturnType.GetGenericArguments().[0])
            | MethodCall (_, MethodName "GroupBy" _, [expr'; Lambda ([paramExpr], bodyExpr) as f']) -> 
                GroupBy (f' :?> LambdaExpression, toQueryExpr expr', typedefof<IGrouping<_, _>>.MakeGenericType [|paramExpr.Type; bodyExpr.Type|])
            | MethodCall (_, MethodName "OrderBy" _, [expr'; Lambda ([paramExpr], bodyExpr) as f']) -> 
                OrderBy (f' :?> LambdaExpression, Order.Ascending, toQueryExpr expr', paramExpr.Type)
            | MethodCall (_, MethodName "Count" _,  [expr'; Lambda ([_], bodyExpr) as f']) -> 
                Count (toQueryExpr expr', bodyExpr.Type)
            | _ -> 
                if expr.Type.IsArray then
                    Source (expr, expr.Type.GetElementType())
                else
                    Source (expr, expr.Type.GetGenericArguments().[0])


