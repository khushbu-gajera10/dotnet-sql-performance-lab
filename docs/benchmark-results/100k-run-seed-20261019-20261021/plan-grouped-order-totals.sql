SELECT "OrderId", sum("Quantity" * "UnitPrice") FROM order_lines WHERE "OrderId" IN (1,2,3,4,5,6,7,8,9,10) GROUP BY "OrderId";
