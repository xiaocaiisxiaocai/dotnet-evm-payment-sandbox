// SPDX-License-Identifier: MIT
pragma solidity 0.8.36;

import {Test} from "forge-std/Test.sol";
import {StdInvariant} from "forge-std/StdInvariant.sol";

import {PaymentRouter} from "../src/PaymentRouter.sol";
import {TestUSDC} from "../src/testtokens/TestUSDC.sol";

contract PaymentHandler {
    uint256 internal constant STARTING_BALANCE = type(uint128).max;
    uint256 internal constant MAX_PAYMENT = 1_000_000e6;

    PaymentRouter public immutable router;
    TestUSDC public immutable token;
    address public immutable merchant;
    uint256 public paymentCount;
    uint256 public totalPaid;

    constructor(PaymentRouter router_, TestUSDC token_, address merchant_) {
        router = router_;
        token = token_;
        merchant = merchant_;

        token.mint(address(this), STARTING_BALANCE);
        token.approve(address(router), type(uint256).max);
    }

    function pay(uint256 rawAmount) external {
        uint256 amount = (rawAmount % MAX_PAYMENT) + 1;
        if (token.balanceOf(address(this)) < amount) return;

        bytes32 paymentId = keccak256(abi.encode("invariant-payment", paymentCount));
        paymentCount += 1;
        totalPaid += amount;
        router.pay(paymentId, address(token), merchant, amount);
    }
}

contract PaymentRouterInvariantTest is StdInvariant, Test {
    PaymentRouter internal router;
    TestUSDC internal token;
    PaymentHandler internal handler;
    address internal merchant;

    function setUp() public {
        router = new PaymentRouter();
        token = new TestUSDC();
        merchant = makeAddr("invariant-merchant");
        handler = new PaymentHandler(router, token, merchant);

        targetContract(address(handler));
    }

    /// @dev The handler only exercises supported direct-payment paths. Directly
    ///      transferring tokens to the Router is outside this invariant and can
    ///      still lock funds because the Router intentionally has no rescue path.
    function invariant_routerNeverCustodiesHandledPayments() public view {
        assertEq(token.balanceOf(address(router)), 0);
    }

    function invariant_merchantBalanceMatchesEveryHandledPayment() public view {
        assertEq(token.balanceOf(merchant), handler.totalPaid());
    }
}
